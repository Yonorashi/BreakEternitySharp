# BreakEternitySharp

A C# port of [break_eternity.js](https://github.com/Patashu/break_eternity.js) 2.1.3 for Unity. `BigDouble` holds numbers up to 10^^1.79e308 (a power tower of 1.79e308 tens) and works like `double` in your code. Basic math does not allocate.

```csharp
using BreakEternity;

BigDouble joule = 100;
BigDouble price = BigDouble.Parse("1e1000");

joule *= BigDouble.Pow(1.15, 20000);    // 9.053303343063e1215
if (joule >= price) joule -= price;
```

This is a new port from the JavaScript source, not a fork of [Pannoniae/BreakEternity.cs](https://github.com/Pannoniae/BreakEternity.cs).

**No maintenance is promised.** I made this for my own incremental game and share it as it is. Issues may go unanswered, so feel free to fork it.

## Install

Copy [`BigDouble.cs`](BigDouble.cs) and [`LRUCache.cs`](LRUCache.cs) anywhere into your `Assets` folder. You need nothing else.

| Optional | What it does |
|---|---|
| [`Editor/BigDoubleDrawer.cs`](Editor/BigDoubleDrawer.cs) | Shows `BigDouble` fields in the Inspector as a text field |
| [`Editor/BigDoubleSelfTest.cs`](Editor/BigDoubleSelfTest.cs) | Adds *Tools > BreakEternity > Run Self-Test* |
| [`Example/BigDoubleExample.cs`](Example/BigDoubleExample.cs) | A small game: buying, display, saving, and migrating old `double` saves |

Keep the two `Editor` files in a folder named `Editor`. [README.de.md](README.de.md) is the full guide, in German.

## Usage

`+ - * / %`, comparisons, `++` and `--` work as usual, also mixed with `int`, `long`, `float` and `double`. Converting back is explicit, because it can lose information: `(double)x`, `x.TryToFloat(out float f)`.

**Numbers beyond `double`.** Literals end at 1.79e308, so use text or a factory method:

```csharp
var a = BigDouble.Parse("1e1000000000");            // throws FormatException on invalid text
bool ok = BigDouble.TryParse(input, out var b);
var c = BigDouble.FromMantissaExponent(1.5, 1e9);   // 1.5e1000000000
var d = BigDouble.Pow10(BigDouble.Pow10(100));      // ee100
var e = BigDouble.Tetrate(10, 5);                   // 10^^5
```

**Math** follows `System.Math`: `BigDouble.Pow`, `Log10`, `Sqrt`, `Floor`, `Min`, `Max`, `Clamp`, `Sin` and so on. Hyperoperators are there too: `Tetrate`, `Slog`, `LayerAdd`, `LambertW`, `Pentate`. So are break_eternity.js's lowercase names, such as `x.add(y)`.

**Buying.** `AffordGeometricSeries` returns the largest count whose total price fits the budget, as computed by `SumGeometricSeries`:

```csharp
BigDouble count = BigDouble.AffordGeometricSeries(joule, priceStart, priceRatio, owned);
joule -= BigDouble.SumGeometricSeries(count, priceStart, priceRatio, owned);
owned += count;
```

**Display.** The library has no game notation; build that yourself from these:

```csharp
BigDouble.Parse("1.23e10000").ToString()        // "1.23e10000", the shortest text that reads back exactly
BigDouble.Parse("1.23e10000").ToString("E2")    // "1.23E+10000"; G, F, N and E follow the culture like double
x.mantissa   x.exponent   x.layer   x.mag   x.sign
```

**Saving.** Use `SerializableBigDouble`, which stores lossless text independent of the culture:

```csharp
[Serializable] public class SaveData { public SerializableBigDouble joule; }

string json = JsonUtility.ToJson(new SaveData { joule = joule });
BigDouble loaded = JsonUtility.FromJson<SaveData>(json).joule;
```

`x.ToSaveString()` and `BigDouble.Parse(text)` do the same for your own formats.

## When you switch from `double`

These all fail silently. Each one came up while moving a real game to `BigDouble`.

1. **Scene and prefab fields.** Change `public double cost` to `public BigDouble cost`, and Unity no longer reads the stored value. The field is 0 and every purchase is free. Keep the `double` field and convert it in `Start`, or enter the values again.
2. **Old saves.** Reading an old `double` save into a class with `BigDouble` or `SerializableBigDouble` fields gives 0 without an error. Read it with the old class, then convert. `Example/BigDoubleExample.cs` shows how.
3. **Plain `BigDouble` fields in JsonUtility saves.** JsonUtility reads some `double` values back one bit off, and in a large number that bit is part of the value's logarithm. In one test, 524 of 5025 values changed. `SerializableBigDouble` changed none.
4. **`double` in between.** A `double` field in the save turns 1e309 into Infinity for good, and `Math.Pow` overflows before `BigDouble` sees the result. Use `BigDouble.Pow`.

Some results also differ from `double`:

| Expression | `double` | `BigDouble` |
|---|---|---|
| `1 / 0` | Infinity | NaN |
| `Log10(0)` | -Infinity | NaN |
| `Round(2.5)` | 2 | 3 |
| `(int)1e300` | int.MinValue | int.MaxValue |
| `(int)NaN` | int.MinValue | 0 |
| `9e15 + 1` | 9000000000000001 | 9e15 |
| `Parse("1,5")` | depends on the culture | 15; use `Parse(text, culture)` for player input |

## Limits

- Precision is that of `double`, about 15 to 16 digits. From 9e15 on, the logarithm is stored, so `1e16 + 1 == 1e16`. The mantissa keeps about 13 digits at 1e1000, 7 at 1e1000000000, and none from about 1e1000000000000000.
- Tetration and pentation with a fractional height are approximations. `Slog`, `LayerAdd`, `Pentate`, `PentaLog` and the super-roots iterate and are much slower than arithmetic (milliseconds, seconds with extreme input), so do not call them every frame.
- Results are not guaranteed bit-identical across platforms, because `Pow`, `Log10` and `Exp` come from each runtime. Saved values are not affected, but lockstep or checksum comparisons between devices would be.
- `BigDouble` is not tested in Burst jobs.

## Performance

Measured in a Windows IL2CPP player on a Ryzen 5 5600X, in nanoseconds, with no allocations:

| Operation | `double` | `BigDouble` | `BigDouble` at 1e1000 |
|---|---:|---:|---:|
| `a + b` | 1.2 | 13.2 | 21.4 |
| `a * b` | 1.5 | 12.5 | 24.6 |
| `Pow` | 18.5 | 59.3 | 53.3 |
| `a >= b` | 1.3 | 7.8 | |
| `Log10` | 5.0 | 14.3 | |

A real game's formulas ran 6 to 7 times slower than with `double`. Its full frames, including the UI, stayed level at about 0.24 ms, because the UI cost far more than the math. Building text allocates, so update display text only when it changes.

## Differences from break_eternity.js

Results match the original down to the last few bits, except for these deliberate fixes and a few smaller ones of the same kind:

| Input | break_eternity.js 2.1.3 | This port |
|---|---|---|
| `Pow(2, 3)` | 7.999999999999999 | 8 |
| `Factorial(5)` | 120.00000000000037 | 120 |
| `Infinity * -2`, `0 * Infinity` | Infinity, Infinity | -Infinity, NaN |
| `NaN <= 1`, `NaN >= 1` | true | false |
| `AffordGeometricSeries(1, 1, 1.1, 0)` | 0 | 1 |
| `AffordGeometricSeries(10, 10, 1, 0)` | NaN | 1 |
| `SumGeometricSeries(1, 13, 1.24, 0)` | 13.000000000000002 | 13 |
| `Sinh(1e-20)`, `Tanh(Infinity)` | 0, NaN | 1e-20, 1 |
| Invalid text | silently 0 or NaN | `FormatException`, or `TryParse` returns false |
| `ToString()` of 1.23e10000 | 1.2299999999997036e10000 | 1.23e10000 (the original text is `valueOf()`) |
| Bisection and recursion that could run forever | can hang | capped, or NaN |

## Testing

| Platform | Result |
|---|---|
| Unity 6.6 Editor, Windows | Self-test passes |
| Unity 6.6 Windows, Mono and IL2CPP | Player checks pass with Managed Stripping High |
| Unity 6.6 Web (WebAssembly) | Player checks pass with Managed Stripping High |
| Unity 6.6 Android, IL2CPP, ARM64 | Player checks pass on one physical device |
| Unity 2022.3 Windows, Mono | Self-test and player checks pass |
| .NET 8 | All 303 tests pass |

**Not tested:** iOS, older Android devices and 32-bit ARM, Burst, Unity older than 2022.3, and the serializers Easy Save, Odin, MessagePack and Protobuf.

The test projects are not in this repository; you can run the self-test above yourself. They covered:

- about 174,000 comparisons against the unmodified JavaScript over 84 functions, with no unexplained difference
- break_eternity.js's own unit tests, ported to C#, with no failures
- mutation testing with 1659 injected faults, which found gaps in the tests but no bug in the library
- a real incremental game moved to `BigDouble` as a test: up to the limit of `double`, the score stayed within 1e-12 of the `double` version and no game decision differed

The port was written by Claude (Anthropic) and reviewed over several rounds by Codex (OpenAI).

## License

MIT, see [LICENSE](LICENSE), which also carries the notice of break_eternity.js. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) covers the part that comes from SciPy.
