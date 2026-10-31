# BigDouble (BreakEternity.cs) – Kurzanleitung

C#-Portierung von **break_eternity.js 2.1.3** für Unity 6 (C# 9, .NET Standard 2.0/2.1, Mono und IL2CPP).
Zahlen bis ca. 10^^1,79e308 (Potenzturm aus 1,79e308 Zehnen) und bis hinunter zu deren Kehrwert.

## Installation

Für die Funktion von BigDouble werden nur **zwei Dateien** gebraucht, z. B. in `Assets/Plugins/BreakEternity`:

| Datei | Inhalt | nötig? |
|---|---|---|
| `BigDouble.cs` | der komplette Typ `BigDouble` (Rechnen, Parsen, Hyperoperationen, C#-API) und `SerializableBigDouble` für Speicherstände | **ja** |
| `LRUCache.cs` | Cache, den `BigDouble.Parse` verwendet | **ja** |
| `Editor/BigDoubleDrawer.cs` | Inspector zeigt `BigDouble`- und `SerializableBigDouble`-Felder als Textfeld (ohne ihn: drei Zahlenfelder `Sign`/`Layer`/`Mag` bzw. der rohe Speichertext) | optional |
| `Editor/BigDoubleSelfTest.cs` | Menü *Tools → BreakEternity → Run Self-Test* | optional, nur zum Prüfen |
| `Example/BigDoubleExample.cs` | kleines vollständiges Beispiel: Ressource, Kaufen, Anzeige, Speichern/Laden mit Migration | optional, zum Abschauen |

Die beiden Editor-Dateien gehören in einen Ordner namens `Editor`; Unity kompiliert sie dann nur im Editor. Das
Beispiel ist eine normale Komponente (`BigDoubleExample` an ein leeres GameObject hängen); es wird nicht gebraucht.

`BigDouble.cs` ist in sechs Abschnitte gegliedert (siehe Kommentar am Dateianfang): Grundlagen und Rechnen,
Operatoren und C#-API, Parsen/Text, Hyperoperationen, Kompatibilitäts-Namen, `SerializableBigDouble`.

## Rechnen wie mit `double`

```csharp
using BreakEternity;

BigDouble joule = 100;            // int, long, float, double werden implizit umgewandelt
BigDouble cost = 10;
BigDouble multiplier = 1.15;

joule += 100;
joule -= cost;
joule *= multiplier;
joule /= 2;
joule++;
if (joule >= cost) joule -= cost;

BigDouble rest = joule % 3;       // Rest wie bei double (Vorzeichen des Dividenden)
BigDouble negativ = -joule;
```

Unterstützt: `+ - * / %`, `+= -= *= /= %=`, `++ --`, unäres `- +`, `== != < <= > >=`,
jeweils auch gemischt mit `int`, `long`, `float`, `double` auf beiden Seiten.

Konvertierungen **aus** BigDouble sind absichtlich explizit, weil sie Information verlieren können:

```csharp
double d = (double)joule;   // > 1,79e308 → Infinity, < 5e-324 → 0
float  f = (float)joule;
long   l = (long)joule;     // abgeschnitten, gesättigt bei long.MinValue/MaxValue, NaN → 0
int    i = (int)joule;
double d2 = joule.ToDouble();

// Sicher für Unity-APIs (Slider, Vector3 …): false bei NaN, Überlauf und wenn ein Wert ≠ 0 zu 0 würde
if (joule.TryToFloat(out float wert)) slider.value = wert;
if (joule.TryToDouble(out double genau)) { /* … */ }
```

`true` heißt „passt in den Wertebereich“, nicht „bitgenau“: Wie bei jeder Umwandlung entsteht der nächstgelegene
double/float-Wert.

### Text: Standardformate wie bei `double`

```csharp
BigDouble x = 1234567.891;
x.ToString("F2")                              // "1234567.89" (aktuelle Kultur, wie double; in de-DE "1234567,89")
x.ToString("N0", new CultureInfo("de-DE"))    // "1.234.568"
$"{x:E3}"                                     // "1.235E+006"
string.Format("{0:G4}", x)                    // "1.235E+06"
BigDouble.Parse("1.23e1000").ToString("E2")   // "1.23E+1000"
x.ToString()                                  // "1234567.891" (ohne Format: kulturunabhängig, siehe unten)
BigDouble.Parse("1.23e10000").ToString()      // "1.23e10000"
```

**`ToString()` ohne Format** liefert wie bei `double` den kürzesten Text, der beim Einlesen exakt denselben Wert
ergibt: kulturunabhängig, verlustfrei (`BigDouble.Parse(x.ToString())` ist wieder genau `x`) und in der
break_eternity-Schreibweise („123.456“, „1.23e10000“, „ee20“). `$"{x}"` und `Debug.Log(x)` zeigen dasselbe.
break_eternity.js schreibt dagegen alle Ziffern der log-Speicherung („1.2299999999997036e10000“); diesen Text liefern
`valueOf()` und `toJSON()`. Kosten pro Aufruf, gemessen im Unity-6.6-Editor (Mono): normale Zahlen ca. 0,7 µs, Werte
ab 9e15 1,7–5 µs (der JS-Text ca. 2–2,5 µs); unter .NET 8 ganze Zahlen 0,03 µs, sonst unter 1 µs. Ein Text pro Frame ist
damit bedeutungslos; für viele Texte in jedem Frame den eigenen NumberFormatter verwenden oder den Text nur bei
Änderungen neu setzen.

Unterstützt: `G`, `F`, `N`, `E` mit optionaler Stellenzahl (0–100); `e`/`g` schreiben ein kleines `e`. Ohne Format
(`$"{joule}"`) kommt die Ausgabe von `ToString()`. Andere Formate (`C`, `P`, `X`, `"0.00"` …) werfen
`FormatException`, statt still ignoriert zu werden. Regeln:
- Genau halbe Werte werden **auf jeder Laufzeit weg von der Null** gerundet (12,5 mit `N0` → „13“; .NET 8 rundet
  bei `double` zur geraden Zahl, Unity/Mono wie BigDouble). Entscheidend ist der exakt gespeicherte Wert: 2,675 ist
  intern 2,67499999…, also `F2` → „2.67“. Unitys eigene `double`-Formatierung rundet über 15 Stellen und zeigt dort
  „2.68“ (im Selbsttest gemessen: 26 von 3000 Texten unterscheiden sich).
- Ein Ergebnis, das auf null rundet, bekommt kein Minuszeichen (`-0.001` mit `F2` → „0.00“).
- `N` setzt negative Werte nach dem Muster der Kultur (`NumberNegativePattern`: „(1,234.50)“, „1,234.50-“,
  „- 1.234,50“ …), genau wie `double`; `F`, `E` und `G` setzen das Minuszeichen wie `double` immer vorn.
- `F`/`N` ab 1e21 und `G` ab 1e15 verwenden die E-Schreibweise („1.23E+1000“) statt nicht gespeicherter Ziffern;
  Werte über 10^9e15 (Layer 2+) zeigen die break_eternity-Schreibweise („ee20“).
- Nur für Anzeigen. Speichern weiterhin mit `ToSaveString()`; Spiel-Notation (K, M, aa …) gehört in den eigenen
  NumberFormatter.

### Eingaben in der Sprache des Spielers

```csharp
var de = new CultureInfo("de-DE");
BigDouble a = BigDouble.Parse("1,5", de);        // 1.5
BigDouble b = BigDouble.Parse("1.234,5", de);    // 1234.5
bool ok = BigDouble.TryParse(eingabe, de, out var c);
BigDouble.Parse("1,5");                          // 15 – der kulturunabhängige Parser für Spielstände bleibt unverändert
```

`Parse(text, kultur)` liest wie `double.Parse(text, kultur)`: Dezimaltrennzeichen, Tausendertrennzeichen,
Vorzeichen und NaN/∞-Symbole der Kultur. Wie bei `double` ist in de-DE `"1.5"` daher 15 (der Punkt ist dort
Tausendertrennzeichen). Alle Schreibweisen von `Parse` funktionieren mit dem Trennzeichen der Kultur
(`"1,5e1000"`, `"(e^7)1234,5"`). Für Spielstände immer das kulturunabhängige `Parse(text)` verwenden.

Nie großzügiger als `double`: Tausendertrennzeichen nur zwischen Ziffern vor dem Dezimaltrennzeichen (`"1.2,3"` in
en-US ist ungültig, ebenso eine Gruppe im Exponenten), und ein Punkt oder Komma, das in der Kultur keine Bedeutung
hat, macht die Eingabe ungültig (`"2.90"` in fr-FR). Zusätzlich werden die negativen Schreibweisen des `N`-Formats der
Kultur gelesen (`"(1,234.50)"`, `"1,234.50-"`), damit jeder mit `ToString(format, kultur)` erzeugte Text wieder
eingelesen werden kann.

### Runden auf Stellen, Interpolation

```csharp
BigDouble.Round(x, 2)                              // 2 Nachkommastellen, halbe Werte weg von der Null
BigDouble.Round(x, 2, MidpointRounding.ToEven)     // wie Math.Round(x, 2)
BigDouble.Round(x, MidpointRounding.ToEven)        // ganze Zahl
BigDouble.Lerp(a, b, t)                            // a + (b − a)·t, t auf 0..1 begrenzt (wie Mathf.Lerp)
BigDouble.LerpUnclamped(a, b, t)
double t = BigDouble.InverseLerp(a, b, wert);      // 0..1, z. B. Fortschrittsbalken
```

`Round(x)` und `Round(x, stellen)` runden halbe Werte weg von der Null (bisheriges Verhalten von `Round`);
`Math.Round` rundet sie zur geraden Zahl – dafür `MidpointRounding.ToEven` angeben. Wie bei `Math.Round` entscheidet
der exakt gespeicherte Wert (`Round(2.675, 2)` = 2,67). Werte ab 2^52 sind bereits ganz und bleiben unverändert.
`Lerp` rechnet vom näheren Endpunkt aus und bleibt deshalb auch nahe t = 1 genau (`Lerp(1e1000, 1, 0.99999999999999)`
≈ 9,992e985) und immer zwischen a und b. Bei Endpunkten mit **verschiedenem Vorzeichen** läuft das Ergebnis durch null;
dort wird genauer gerechnet: Mit einer normalen Zahl (unter 9e15) als Endpunkt ist das Ergebnis exakt gerundet, als
hätte man mit unbegrenzter Genauigkeit gerechnet (`Lerp(-8e15, 8e15, 0.5)` = 0, einen Schritt daneben
8e15 · 2^-52 = 1,776…); bei zwei großen Endpunkten wird in ihrer Logarithmus-Form gerechnet (`Lerp(-1e20, 1e20,
0.50000000000001)` = 1998401,4443252818). Kleine Ergebnisse werden dabei nie pauschal auf null gesetzt. Bei Türmen
(ab 10^9e15) entscheidet der größere Endpunkt, ein Faktor ändert dort nichts mehr: `Lerp(-ee21, ee20, 0.25)` ≈ −ee21.
Zwischen sehr verschieden großen Zahlen wird das Ergebnis von der größeren
bestimmt (`Lerp(1, 1e1000, 0.5)` ≈ 5e999); für eine „hochzählende“ Anzeige die Exponenten interpolieren:
`BigDouble.Pow10(BigDouble.Lerp(BigDouble.Log10(a), BigDouble.Log10(b), t))`.

## Sehr große Zahlen erzeugen

`double`-Literale enden bei 1,79e308. Größere Werte über Strings oder Fabrikmethoden:

```csharp
var a = new BigDouble("1e1000000000");               // wirft FormatException bei ungültigem Text
var b = BigDouble.Parse("1e1000000000000000");
bool ok = BigDouble.TryParse(eingabe, out var c);    // false bei ungültigem Text (c = 0)
var d = BigDouble.FromMantissaExponent(1.5, 1e9);    // 1,5 · 10^1000000000
var e = BigDouble.Pow10(BigDouble.Pow10(100));       // 10^10^100
var f = BigDouble.Tetrate(10, 5);                    // 10^^5
```

Alle Wege liefern identische Werte (`new BigDouble("1e1000000000") == BigDouble.FromMantissaExponent(1, 1e9)`).

Parse-Formate (Groß-/Kleinschreibung egal, Dezimaltrennzeichen immer `.`, Kommas werden ignoriert):

| Format | Beispiel | Bedeutung |
|---|---|---|
| Zahl | `123.45`, `-1.5e-10`, `1e1000000000` | beliebig große Exponenten |
| Exponentenkette | `1e1e10`, `ee15.5`, `-eee10`, `2e3e10` | 10^10^10, … |
| Layer-Notation | `(e^7)1234.5`, `-(e^7)1234.5` | Ausgabe von `ToString`/`ToSaveString` |
| Potenz | `2^1024` | |
| Tetration | `10^^5`, `10^^5;3` (Payload 3) | |
| Pentation | `2^^^3` | |
| pt/F | `5pt3`, `5p3`, `3F5`, `(3)F(5)` | 10^^5 mit Payload 3 |
| Sonderwerte | `NaN`, `Infinity`, `-Infinity` | |

## Mathematik

Statische API im Stil von `System.Math`:

```csharp
BigDouble.Pow(a, b)      BigDouble.Pow10(x)    BigDouble.Exp(x)      BigDouble.Root(x, n)
BigDouble.Sqrt(x)        BigDouble.Cbrt(x)     BigDouble.Square(x)   BigDouble.Cube(x)
BigDouble.Log10(x)       BigDouble.Log2(x)     BigDouble.Log(x)      BigDouble.Log(x, basis)   BigDouble.Ln(x)
BigDouble.Floor(x)       BigDouble.Ceiling(x)  BigDouble.Round(x)    BigDouble.Truncate(x)
BigDouble.Abs(x)         BigDouble.Sign(x)     BigDouble.Min(a, b)   BigDouble.Max(a, b)   BigDouble.Clamp(x, lo, hi)
BigDouble.Factorial(x)   BigDouble.Gamma(x)    BigDouble.PositiveLog10(x)   BigDouble.ApproximatelyEquals(a, b)
BigDouble.Sin/Cos/Tan/Asin/Acos/Atan/Sinh/Cosh/Tanh/Asinh/Acosh/Atanh(x)
BigDouble.IsNaN(x)  IsFinite(x)  IsInfinity(x)  IsPositiveInfinity(x)  IsNegativeInfinity(x)  IsZero(x)
```

Hyperoperationen: `Tetrate`, `IteratedExp`, `IteratedLog`, `Slog`, `LayerAdd`, `LayerAdd10`, `LambertW`,
`SuperSqrt`, `LinearSuperRoot`, `Pentate`, `PentaLog`, `LinearPentaRoot`, `increasingInverse`.

`Pow` mit zwei normalen Zahlen rechnet direkt mit `Math.Pow`: `Pow(10, 3) == 1000` und `Pow(2, 52)` sind exakt,
`Pow(1.15, 100)` ist bitgleich zu `Math.Pow(1.15, 100)`. Erst wenn das Ergebnis nicht mehr in einen `double` passt,
wird über Logarithmen gerechnet.

### Gleichheit mit Toleranz

- `BigDouble.ApproximatelyEquals(a, b, toleranz)` vergleicht **relativ zum Wert**: `true`, wenn
  |a − b| ≤ toleranz · max(|a|, |b|). Das ist die Funktion für Spiellogik (`1e1000` und `1.05e1000` sind bei
  Toleranz 0,01 *nicht* gleich).
- `a.eqTolerance(b, toleranz)` / `cmpTolerance` verhalten sich wie im JS-Original: verglichen wird der intern
  gespeicherte Betrag `mag`, ab Layer 1 also der **Exponent**. `1e1000` und `1e1001` gelten dort bei Toleranz 0,01 als
  gleich. Die Hyperoperationen (`slog`, Super-Wurzeln, Lambert W) sind auf genau dieses Verhalten abgestimmt,
  deshalb bleibt es unverändert.

### Kaufformeln

`AffordGeometricSeries`, `SumGeometricSeries`, `AffordArithmeticSeries`, `SumArithmeticSeries`,
`EfficiencyOfPurchase`.

Garantie: `Afford…(budget, …)` liefert die **größte ganze Anzahl n ≥ 0, für die `Sum…(n, …) ≤ budget`** ist – mit
genau der `Sum…`-Funktion dieser Bibliothek. Mit genau dem Preis des ersten Gegenstands ist also 1 Stück kaufbar,
und nach dem Kauf von `Afford…` Stück reicht der Rest nie für ein weiteres. Exakt gilt das bis 2^53 (~9e15) Stück;
darüber liefert die Formel eine Näherung (dort ist ±1 Stück nicht mehr darstellbar). Vorausgesetzt:
Startpreis > 0, Faktor ≥ 1 (geometrisch), Preisschritt ≥ 0 (arithmetisch). Negatives Budget ergibt 0.

`SumGeometricSeries` addiert bei normalen Zahlen bis 32 Stück die Einzelpreise `Startpreis · Faktor^(Bestand + j)`
einzeln auf, genau wie ein Spiel sie Stück für Stück berechnet: Zwei Gegenstände zu 10 und 10 · 1,00000001 kosten
exakt `10 + 10 * 1.00000001`. Für mehr Stück gilt eine numerisch stabile Formel (relativer Fehler unter 1e-14, auch
für Faktoren sehr nahe 1). Die Kosten von 33 und mehr Stück können daher im letzten Bit von einer selbst
aufaddierten Summe abweichen.

Das gilt für **jede Größe des Startpreises und Bestands** (1e1000, 1e-100, Bestand 1e20 …): Der Reihenfaktor
1 + r + … + r^(n−1) wird unabhängig vom Preis genau berechnet und erst danach mit dem Preis multipliziert. Die
Genauigkeit ist dann nur durch die Speicherung großer Zahlen begrenzt (bei 1e1000 ca. 1e-12 relativ):
`AffordGeometricSeries(2e1000, 1e1000, 1.00000001, 0)` ist 1, weil zwei Stück 2,00000001e1000 kosten.

Konstanten: `Zero`, `One`, `MinusOne`, `Two`, `Ten`, `NaN`, `PositiveInfinity`, `NegativeInfinity`, `MaxValue`, `MinValue`.

Die bisherigen kleingeschriebenen Methoden (`x.add(y)`, `x.pow(2)`, `BigDouble.tetrate(...)`, `x.eqTolerance(...)`)
funktionieren weiterhin.

Für die Anzeige (`NumberFormatter`) sind gedacht: `x.mantissa` (in [1, 10) für Layer 0/1), `x.exponent`,
`x.layer`, `x.mag`, `x.sign`.

## Speichern und Laden

**Empfohlen: `SerializableBigDouble` im Speicherstand.** Speichert den Wert als verlustfreien, kulturunabhängigen
Text; vollständig getestet und unabhängig davon, wie Unity `double`-Werte schreibt.

```csharp
[Serializable]
public class SaveData
{
    public SerializableBigDouble joule;
    public SerializableBigDouble[] generators;
}

// speichern
var save = new SaveData { joule = joule };          // implizite Umwandlung BigDouble → SerializableBigDouble
string json = JsonUtility.ToJson(save);

// laden
var loaded = JsonUtility.FromJson<SaveData>(json);
BigDouble joule = loaded.joule;                     // implizite Umwandlung zurück
```

JSON: `{"joule":{"value":"(e^1)1000000000"}}`. Fehlt das Feld (alter Spielstand), ergibt es 0.
Ist der Text beschädigt, wirft die Umwandlung `FormatException`; mit `loaded.joule.TryGetValue(out var v)`
lässt sich das ohne Exception prüfen.

**Explizit über Strings** (z. B. für eigene Formate, PlayerPrefs, MessagePack):

```csharp
string text = joule.ToSaveString();                 // verlustfrei, z. B. "123.456" oder "(e^1)1000000000"
BigDouble back = BigDouble.Parse(text);             // bitgenau identisch
```

**Andere Serializer.** Die Regel: Ein Wert kommt bitgenau zurück, oder der Serializer meldet einen Fehler; nie still 0.

| Serializer | `BigDouble`-Feld | `SerializableBigDouble` | Hinweis |
|---|---|---|---|
| Newtonsoft Json.NET | exakt: `"joule":"(e^1)1000"` | exakt: `{"value":"(e^1)1000"}` wie JsonUtility | braucht keine Einstellung; liest auch einfache Zahlen, ein früheres `double`-Feld behält also seinen Stand |
| `XmlSerializer` | exakt | exakt | im Mono-Player unter Managed Stripping High scheitert er auch ohne BigDouble: dann niedrigere Stufe oder `link.xml`; unter IL2CPP auch mit High geprüft |
| `DataContractSerializer`, `BinaryFormatter` | exakt | exakt | |
| `System.Text.Json` (nur .NET, nicht in Unity enthalten) | exakt | exakt | Felder nur mit `IncludeFields = true` |
| JsonUtility | Letztes Bit kann abweichen, siehe unten | exakt | |
| Easy Save, Odin, MessagePack, Protobuf | **nicht getestet** | **nicht getestet** | vor dem ersten Release einen Testspielstand laden; im Zweifel `ToSaveString()` als `string` speichern |

Newtonsoft nutzt dafür den `TypeConverter` von BigDouble (`BigDoubleTypeConverter`), die Bibliothek braucht das Paket
also nicht. Ein Spielstand aus der Zeit davor enthält ein Objekt wie `{"sign":1,"layer":1,...}`; das meldet jetzt einen
Fehler, statt wie zuvor still 0 zu lesen.

**Direkt als BigDouble-Feld** (`public BigDouble joule;` in einer `[Serializable]`-Klasse): Unity serialisiert die
drei privaten `double`-Felder `_sign`, `_layer`, `_mag` (JSON: `{"_sign":1.0,"_layer":1.0,"_mag":1000000000.0}`).
Das ist für Inspector, Szenen und ScriptableObjects gedacht, **nicht für Speicherstände**:
- In Unity 6.6 geprüft: JsonUtility schreibt `double`-Werte vollständig, liest aber manche davon um eine Stelle im
  letzten Bit falsch zurück (in 4 von 17 BigDouble-Testwerten). Bei normalen Zahlen ist das vernachlässigbar (~1e-16),
  aber bei Layer 1 steckt die Mantisse in den hinteren Stellen von `_mag`: Je größer der Exponent, desto stärker ändert
  sich die Zahl – z. B. wurde -1,5e1000000000000000 nach dem Laden zu -1e1000000000000000.
- NaN/Infinity schreibt JsonUtility als `NaN`/`Infinity` ohne Anführungszeichen. Unity liest das wieder ein, andere
  JSON-Werkzeuge (Newtonsoft, Web-Backends) lehnen solche Dateien aber ab.

Für Werte, die von Hand im Inspector eingetragen werden (Kosten, Startwerte), reichen 15 Stellen; für Spielstände
`SerializableBigDouble` oder `ToSaveString()` verwenden. Das Menü *Tools → BreakEternity → Run Self-Test* zeigt
das Verhalten deiner Unity-Version unter „INFO“.

Die Feldnamen `_sign`, `_layer`, `_mag` und `value` nicht umbenennen, sonst werden bestehende Daten als 0 gelesen.

### Alte `double`-Spielstände übernehmen

Bewährtes Muster (vollständig in `Example/BigDoubleExample.cs`, in Unity 6.6 geprüft):

```csharp
[Serializable] public class SaveData                 // aktuelles Format, Version 2
{
    public const int CurrentVersion = 2;
    public int version;                              // ohne Startwert: alte Stände ohne Feld → 0
    public SerializableBigDouble joule;
    public SerializableBigDouble multiplier = 1;     // neues Feld: neutraler Startwert 1
}
[Serializable] public class SaveDataV1               // altes Format, nur noch zum Lesen
{
    public int version;
    public double joule;                             // gleicher Name, aber double
}

int version = JsonUtility.FromJson<SaveDataV1>(json).version;
SaveData data;
if (version >= 2) data = JsonUtility.FromJson<SaveData>(json);
else data = new SaveData { joule = JsonUtility.FromJson<SaveDataV1>(json).joule };   // double → BigDouble
if (string.IsNullOrEmpty(data.multiplier.SerializedValue)) data.multiplier = 1;       // fehlendes Feld absichern
data.version = SaveData.CurrentVersion;
```

- Alte und neue Felder mit gleichem Namen, aber anderem Typ nie in **einer** Klasse lesen: Ein alter Spielstand wird
  mit der alten Klasse gelesen und umgewandelt.
- Ein fehlendes `SerializableBigDouble`-Feld ergibt 0. JsonUtility behält zwar den Startwert der Klasse
  (`= 1`, im Selbsttest unter INFO geprüft), die ausdrückliche Prüfung oben schützt aber auch gegen andere Serializer.
- Preise und Zwischenwerte durchgehend mit BigDouble rechnen (`BigDouble.Pow` statt `Math.Pow`), sonst entsteht
  Infinity schon vor der Umwandlung.
- **Felder in Szenen und Prefabs:** Wird aus `public double kosten` ein `public BigDouble kosten`, liest Unity den in der
  Szene gespeicherten Wert nicht mehr: Das Feld ist ohne Fehlermeldung 0, jeder Kauf wäre kostenlos. Entweder das Feld als
  `double` im Inspector lassen und zur Laufzeit einen BigDouble daraus machen (`kostenJetzt = kosten;` in `Start`), oder
  die Werte nach dem Umstellen neu eintragen. In einem echten Projekt geprüft.

Im **Inspector** erscheint ein BigDouble (und ein `SerializableBigDouble`) als Textfeld; jede von `Parse` akzeptierte
Schreibweise kann eingegeben werden (`1e1000`, `ee20`, `10^^3` …); ungültige Eingaben ändern nichts. Angezeigt wird
`ToString()`: der kürzeste Text, der beim Einlesen exakt denselben Wert ergibt (`1.5e1000000000` statt
`(e^1)1000000000.1760913`). Im Spiel zeigt `ToString()` deshalb denselben Text wie der Inspector. Die Werte werden nur
beim Ändern geschrieben, das Anzeigen verliert nichts.

## Getestet auf

| Plattform | Ergebnis |
|---|---|
| Unity 6.6 Editor, Windows | Selbsttest bestanden |
| Unity 6.6 Windows, Mono und IL2CPP | Player-Prüfungen bestanden, Managed Stripping High |
| Unity 6.6 Web (WebAssembly) | Player-Prüfungen bestanden, Managed Stripping High |
| Unity 6.6 Android (IL2CPP, ARM64) | Player-Prüfungen bestanden auf einem echten Gerät, Managed Stripping High |
| Unity 2022.3 Windows, Mono | Selbsttest und Player-Prüfungen bestanden |
| .NET 8 | vollständige Testsuite |

Die Bibliothek kompiliert ab C# 8 (Unity 2020.2 und neuer); ältere Editoren als 2022.3 sind nicht geprüft.

## Genauigkeit

Gemessen gegen eine unabhängige Rechnung mit 256 Bit. 1 ulp ist das letzte Bit des
gespeicherten Werts, ein exakt gerundetes Ergebnis weicht höchstens 0,5 ulp ab.

| Funktion | größte gemessene Abweichung |
|---|---:|
| `+ − * /` normaler Zahlen, `Log10`, `Pow10`, `Sqrt`, `Sqr`, Kehrwert | 0,5 ulp |
| `Exp`, `Sin`, `Cos`, `Tan`, `Asin`, `Acos`, `Atan`, `Sinh`, `Cosh`, `Tanh` | unter 1 ulp |
| `Pow(faktor, n)` (Kostenformel), `Ln`, `Log2`, `a + b` | unter 1,5 ulp |
| `Asinh`, `Acosh`, `Atanh` | 2,3 ulp |
| `Lerp` durch null mit einem normalen Endpunkt | 0,5 ulp |
| `Tetrate` ganzzahlige Höhe, `LambertW` | 7,5 ulp |
| Ergebnis unter 9e15 aus Werten ab 9e15 (`a * b`, `a / b`, `a - b`, `Pow`, `Cbrt`) | bis 25 ulp, etwa 5e-15 relativ; genauer ist der Eingabewert selbst nicht gespeichert |
| `Gamma`, `Factorial` | ganze Zahlen 0,5 ulp, sonst bis 68 ulp (1,5e-14 relativ) |
| `LnGamma` | im Mittel 1 ulp, nahe den Nullstellen 1 und 2 bis 434 ulp |

Tetration und Pentation mit gebrochener Höhe, `slog` und `linear_sroot` sind Näherungen ohne mathematisch exakten
Vergleichswert; sie stimmen mit break_eternity.js überein.

## Einschränkungen

- **Genauigkeit wie `double`:** ca. 15–16 signifikante Stellen. `1e300 + 1 == 1e300`, `x++` ändert ab ca. 9e15 nichts mehr.
- **Exakte Werte nur im normalen Bereich:** Zahlen mit Betrag zwischen 1,1e-16 und 9e15 werden exakt als `double`
  gespeichert. Darüber/darunter wird log10 gespeichert: ~15 signifikante Stellen, `(double)` kann im letzten Bit
  abweichen; Werte innerhalb ~1e-13 von `double.MaxValue` können bei der Rückumwandlung zu Infinity werden.
- **Mantisse bei großen Exponenten:** Bei Layer 1 bleiben ca. 16 − log10(Exponent) Stellen der Mantisse übrig
  (Exponent 1000 → ~13 Stellen, 1e9 → ~7 Stellen, ab ~1e15 keine). Ab Layer 2 ist `mantissa` nur noch das Vorzeichen.
- **`ToString()` ist verlustfrei, aber kein Speicherformat:** Für Spielstände `ToSaveString()` bzw.
  `SerializableBigDouble` verwenden (festes Format, schneller). `valueOf()`/`toJSON()` liefern den Text von
  break_eternity.js und sind nicht verlustfrei.
- **Division durch 0 ergibt NaN** (wie break_eternity.js, nicht ±Infinity wie `double`). `x % 0` ergibt 0, `0^x` ergibt 0.
- **NaN-Vergleiche wie bei `double`:** `<`, `<=`, `>`, `>=`, `==` mit NaN sind `false`; `Equals`/`CompareTo` behandeln
  NaN als gleich bzw. kleinsten Wert (für Sortierung und Dictionaries).
- **`Round` rundet .5 von der Null weg** (wie JavaScript), nicht zur geraden Zahl wie `Math.Round`.
- **Riesige Zahlen modulo kleiner Zahlen** (`1e1000 % 7`) sind nicht berechenbar und ergeben 0.
- **Tetration mit nicht ganzzahliger Höhe** ist eine Näherung (analytisch für Basis ≤ 10, sonst linear);
  Pentation mit nicht ganzzahliger Höhe immer linear. `slog`, `layeradd`, `pentate`, `penta_log` und Super-Wurzeln sind
  iterativ und deutlich langsamer als Grundrechenarten: nicht pro Frame aufrufen. Üblich sind Millisekunden, mit extremen
  Eingaben gemessen bis ~6 s für `pentate`, ~5 s für `penta_log` und ~2,5 s für `linear_sroot` (im Normalfall weit weniger).
- **Rechenergebnisse sind zwischen Plattformen nicht bitgleich garantiert:** `Pow`, `Log10`, `Exp` kommen aus der
  Mathe-Bibliothek der jeweiligen Laufzeit. Gemessen gegen .NET 8, von 6000 Ergebnissen anders: Windows-IL2CPP 1 (1 ulp),
  Windows-Mono 2 (bis 6 ulp bei `tetrate`), Web und Android je 19 (bis 2 ulp, `tetrate` 6). Gespeicherte Werte betrifft das nicht; für
  synchrone Berechnungen über mehrere Geräte (Lockstep, Vergleich von Prüfsummen) aber schon.
- **Android und iOS:** Android lief nur auf einem Gerät (ARM64); ältere Geräte und 32-Bit-ARM sind ungeprüft.
  iOS ist ungetestet.
- **Burst:** BigDouble ist für verwalteten Code gedacht (Strings, Cache, `Math`). In Burst-Jobs ist es nicht getestet.
- **`Gamma`/`Factorial` sind für ganze Zahlen exakt** (`Factorial(5)` = 120), sonst auf etwa 1e-14 genau, `LnGamma`
  nahe seinen Nullstellen 1 und 2 entsprechend weniger.
- **Iterierter Logarithmus/Tetration mit Basis < 1 und gebrochener Höhe** ergibt NaN (im JS-Original undefiniert
  und extrem langsam).
- **Trigonometrie** ist nur für normale Zahlen sinnvoll; `Sin/Cos/Tan` von NaN oder ±Infinity ergeben NaN.
  Die hyperbolischen Funktionen rechnen bei normalen Zahlen mit `Math.Sinh/Cosh/Tanh` bzw. stabilen Formeln
  (`Sinh(1e-20) == 1e-20`, `Tanh(±Infinity) == ±1`, `Atanh` genau bis unmittelbar an ±1 und exakt ungerade:
  `Atanh(-x) == -Atanh(x)`) und weichen deshalb in den letzten Stellen vom JS-Original ab.
- **`eqTolerance` vergleicht ab Layer 1 Exponenten**, nicht Werte (siehe „Gleichheit mit Toleranz“).
- **`Parse(text)` kennt nur `.` als Dezimaltrennzeichen**: `Parse("1,5")` ergibt 15 (Kommas werden wie
  Tausendertrennzeichen ignoriert). Für Eingaben in der Sprache des Spielers `Parse(text, kultur)` verwenden.
- **`toFixed`/`toExponential`/`toPrecision`** folgen JavaScript (aus dem exakten Wert, halbe Werte zur größeren Zahl:
  `2.5.toFixed(0)` = „3“, `-0.0001.toFixed(2)` = „-0.00“), auf jeder Laufzeit gleich. Für Layer 1+ verwenden sie wie
  das Original die break_eternity-Schreibweise.
- **Unity-Inspector** bearbeitet nur einzelne Werte; Mehrfachauswahl mit unterschiedlichen Werten wird leer angezeigt.
