#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace BreakEternity.EditorTools
{
    /// <summary>Data class used by the self-test to check Unity's JsonUtility.</summary>
    [Serializable]
    public class BigDoubleSelfTestData
    {
        public BigDouble direct;
        public BigDouble[] array;
        public List<BigDouble> list;
        public SerializableBigDouble asString;
    }

    /// <summary>Plain double field, to measure how precisely JsonUtility writes doubles.</summary>
    [Serializable]
    public class BigDoubleSelfTestDouble
    {
        public double value;
    }

    /// <summary>A field with an initializer, to see whether JsonUtility keeps it when the JSON lacks the field.</summary>
    [Serializable]
    public class BigDoubleSelfTestDefaults
    {
        public int marker;
        public SerializableBigDouble multiplier = 1;
    }

    /// <summary>
    /// Menu "Tools/BreakEternity/Run Self-Test": checks the parts that can only be verified inside Unity
    /// (Mono/IL2CPP number formatting and parsing, JsonUtility) and prints a report to the console.
    /// FAIL lines are library errors. INFO lines describe Unity behaviour the library cannot change.
    /// </summary>
    public static class BigDoubleSelfTest
    {
        private static int s_passed;
        private static int s_failed;
        private static StringBuilder s_log;

        [MenuItem("Tools/BreakEternity/Run Self-Test")]
        public static void Run()
        {
            string summary = RunAndReport();
            if (s_failed == 0)
            {
                Debug.Log(summary);
            }
            else
            {
                Debug.LogError(summary);
            }
        }

        /// <summary>Runs all checks and returns the report (also usable from -executeMethod / batch mode).</summary>
        public static string RunAndReport()
        {
            s_passed = 0;
            s_failed = 0;
            s_log = new StringBuilder();
            s_log.AppendLine("Unity " + Application.unityVersion + ", " + Environment.Version);
            var originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                RunAll("invariant culture");
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                BigDouble.ClearParseCache();
                RunAll("de-DE culture");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
            }

            return "BreakEternity self-test: " + s_passed + " passed, " + s_failed + " failed\n" + s_log;
        }

        private static void RunAll(string context)
        {
            s_log.AppendLine("--- " + context + " ---");

            // Reported bug: string and factory method must agree.
            Check("Parse(\"1e1000000000\") == FromMantissaExponent(1, 1e9)",
                () => BigDouble.Parse("1e1000000000") == BigDouble.FromMantissaExponent(1, 1e9));
            Check("new BigDouble(\"1e1000000000\").ToString()", () => new BigDouble("1e1000000000").ToString() == "1e1000000000");
            Check("Parse(\"1e309\") is finite", () => BigDouble.IsFinite(BigDouble.Parse("1e309")));
            Check("Parse(\"1.5\") == 1.5 (culture independent)", () => BigDouble.Parse("1.5") == 1.5);
            Check("ToString(1.5) == \"1.5\"", () => new BigDouble(1.5).ToString() == "1.5");

            // Operators like double
            Check("operator example matches double", () =>
            {
                BigDouble joule = 100;
                BigDouble cost = 10;
                joule += 100;
                joule -= cost;
                joule *= 1.15;
                joule /= 2;
                if (joule >= cost)
                {
                    joule -= cost;
                }
                double d = ((100.0 + 100 - 10) * 1.15 / 2) - 10;
                return joule.ToDouble() == d;
            });

            // Mantissa of exact powers of ten (depends on the runtime's Math.Log10)
            Check("mantissa of 1e-15..1e15 within [1, 10)", () =>
            {
                for (int p = -15; p <= 15; p++)
                {
                    double m = new BigDouble(Math.Pow(10, p)).mantissa;
                    if (!(m >= 1 && m < 10))
                    {
                        return false;
                    }
                }
                return true;
            });

            // Findings of the independent review (pow exactness, purchase boundaries, hyperbolic functions, special values)
            Check("Pow(2,3) == 8, Pow(-2,3) == -8, Pow(2,10) == 1024, Floor(Pow(2,3)) == 8", () =>
                BigDouble.Pow(2, 3) == 8 && BigDouble.Pow(-2, 3) == -8 && BigDouble.Pow(2, 10) == 1024 && BigDouble.Floor(BigDouble.Pow(2, 3)) == 8);
            Check("Pow(1.15, 100) == Math.Pow(1.15, 100)", () => BigDouble.Pow(1.15, 100).ToDouble() == Math.Pow(1.15, 100));
            Check("AffordArithmeticSeries(13, 13, 3, 0) == 1 and (1e100, 1e100, 1, 0) == 1", () =>
                BigDouble.AffordArithmeticSeries(13, 13, 3, 0) == 1 && BigDouble.AffordArithmeticSeries(BigDouble.Parse("1e100"), BigDouble.Parse("1e100"), 1, 0) == 1);
            Check("AffordGeometricSeries at its own SumGeometricSeries (7 ratios x 100 counts)", () =>
            {
                foreach (double ratio in new[] { 1.01, 1.1, 1.15, 1.2, 1.5, 2, 10 })
                {
                    for (int n = 1; n <= 100; n++)
                    {
                        if (BigDouble.AffordGeometricSeries(BigDouble.SumGeometricSeries(n, 10, ratio, 0), 10, ratio, 0) != n)
                        {
                            s_log.AppendLine("    mismatch: ratio " + ratio + ", n " + n);
                            return false;
                        }
                    }
                }
                return true;
            });
            Check("constant prices: AffordGeometricSeries(100, 10, 1, 0) == 10, AffordArithmeticSeries(100, 10, 0, 0) == 10", () =>
                BigDouble.AffordGeometricSeries(100, 10, 1, 0) == 10 && BigDouble.AffordArithmeticSeries(100, 10, 0, 0) == 10);
            Check("negative budget: AffordGeometricSeries(-100, 10, 2, 0) == 0, AffordArithmeticSeries(-100, 10, 3, 0) == 0", () =>
                BigDouble.AffordGeometricSeries(-100, 10, 2, 0) == 0 && BigDouble.AffordArithmeticSeries(-100, 10, 3, 0) == 0);
            Check("Sinh/Asinh/Atanh(1e-20) == 1e-20, Sinh(1e-10) == Math.Sinh(1e-10)", () =>
                BigDouble.ApproximatelyEquals(BigDouble.Sinh(1e-20), 1e-20, 1e-12) && BigDouble.ApproximatelyEquals(BigDouble.Asinh(1e-20), 1e-20, 1e-12)
                && BigDouble.ApproximatelyEquals(BigDouble.Atanh(1e-20), 1e-20, 1e-12) && BigDouble.Sinh(1e-10).ToDouble() == Math.Sinh(1e-10));
            // Second review: price ratios near 1, atanh near -1
            Check("ratio near 1: SumGeometricSeries(2, 10, 1.00000001, 0) == 10 + 10 * 1.00000001, AffordGeometricSeries(20, ...) == 1", () =>
                BigDouble.SumGeometricSeries(2, 10, 1.00000001, 0).ToDouble() == 10 + 10 * 1.00000001
                && BigDouble.AffordGeometricSeries(20, 10, 1.00000001, 0) == 1
                && BigDouble.SumGeometricSeries(2, 1, 1.0000001, 0).ToDouble() == 1 + 1.0000001);
            Check("Atanh(-0.9999999999999999) == -18.714973875118524, Atanh(-x) == -Atanh(x)", () =>
                BigDouble.ApproximatelyEquals(BigDouble.Atanh(-0.9999999999999999), -18.714973875118524, 1e-14)
                && BigDouble.Atanh(-0.99999999).ToDouble() == -BigDouble.Atanh(0.99999999).ToDouble());
            // Third review: prices beyond layer 0, runtime independent text, convenience API
            Check("large prices: AffordGeometricSeries(2e1000, 1e1000, 1.00000001, 0) == 1", () =>
                BigDouble.AffordGeometricSeries(BigDouble.Parse("2e1000"), BigDouble.Parse("1e1000"), 1.00000001, 0) == 1
                && BigDouble.SumGeometricSeries(2, BigDouble.Parse("1e1000"), 1.00000001, 0) > BigDouble.Parse("2e1000"));
            Check("toFixed/toExponential like JavaScript: 2.5 -> \"3\" / \"3e+0\", 12.5 -> \"1e+1\", 1.005 -> \"1.00\"", () =>
                ((BigDouble)2.5).toFixed(0) == "3" && ((BigDouble)2.5).toExponential(0) == "3e+0"
                && ((BigDouble)12.5).toExponential(0) == "1e+1" && ((BigDouble)1.005).toFixed(2) == "1.00");
            Check("IFormattable: {0:F2} of 12.5 is \"12.50\", N2 in de-DE is \"1.234.567,89\", 1.23e1000 \"E3\" is \"1.230E+1000\"", () =>
                string.Format(CultureInfo.InvariantCulture, "{0:F2}", (BigDouble)12.5) == "12.50"
                && ((BigDouble)1234567.891).ToString("N2", new CultureInfo("de-DE")) == "1.234.567,89"
                && BigDouble.Parse("1.23e1000").ToString("E3", CultureInfo.InvariantCulture) == "1.230E+1000");
            Check("Parse(\"1,5\", de-DE) == 1.5, Parse(\"1,5\") == 15 (save format unchanged)", () =>
                BigDouble.Parse("1,5", new CultureInfo("de-DE")) == 1.5 && BigDouble.Parse("1,5") == 15);
            Check("TryToDouble(1e1000) and TryToFloat(1e300) are false, TryToFloat(1.5) is true", () =>
                !BigDouble.Parse("1e1000").TryToDouble(out _) && !((BigDouble)1e300).TryToFloat(out _)
                && ((BigDouble)1.5).TryToFloat(out float f) && f == 1.5f);
            Check("Round(0.125, 2) == 0.13, Round(0.125, 2, ToEven) == 0.12, Round(2.675, 2) == 2.67", () =>
                BigDouble.Round(0.125, 2) == 0.13 && BigDouble.Round(0.125, 2, MidpointRounding.ToEven) == 0.12 && BigDouble.Round(2.675, 2) == 2.67);
            Check("Lerp(10, 20, 0.25) == 12.5, Lerp clamps t, InverseLerp(10, 20, 15) == 0.5", () =>
                BigDouble.Lerp(10, 20, 0.25) == 12.5 && BigDouble.Lerp(10, 20, 2) == 20 && BigDouble.InverseLerp(10, 20, 15) == 0.5);
            // Fourth review: Lerp near the end points, NumberNegativePattern, stricter culture parser
            Check("Lerp(1e1000, 1, 0.99999999999999) is about 9.992e985 (not 0)", () =>
            {
                var result = BigDouble.Lerp(BigDouble.Parse("1e1000"), 1, 0.99999999999999);
                return result >= 1 && Math.Abs(BigDouble.Log10(result).ToDouble() - (1000 + Math.Log10(1 - 0.99999999999999))) < 1e-12;
            });
            Check("Factorial(5) == 120, Pow10(-13) and Sqrt(1e-30) without detour, TypeConverter present (Newtonsoft uses it)", () =>
            {
                var converter = System.ComponentModel.TypeDescriptor.GetConverter(typeof(BigDouble));
                return ((BigDouble)5).factorial().ToDouble() == 120 && ((BigDouble)(-13)).pow10().ToDouble() == Math.Pow(10, -13)
                    && BigDouble.Parse("1e-30").sqrt().mag == Math.Pow(10, -15)
                    && converter is BigDoubleTypeConverter && converter.ConvertToInvariantString(BigDouble.Parse("1e1000")) == "(e^1)1000";
            });
            Check("Lerp through zero: (-8e15, 8e15, 0.5) == 0, one step beside it 8e15 * 2^-52, (-1e20, 1e20, 0.50000000000001) about 1998401.444", () =>
            {
                double beside = 0.5 + Math.Pow(2, -53);
                var large = BigDouble.Lerp(BigDouble.Parse("-1e20"), BigDouble.Parse("1e20"), 0.50000000000001).ToDouble();
                return BigDouble.Lerp(-8e15, 8e15, 0.5).sign == 0 && BigDouble.LerpUnclamped(8e15, -8e15, 0.5).sign == 0
                    && BigDouble.Lerp(-8e15, 8e15, beside).ToDouble() == 8e15 * Math.Pow(2, -52)
                    && BigDouble.Lerp(BigDouble.Parse("-1e1000"), BigDouble.Parse("1e1000"), 0.5).sign == 0
                    && Math.Abs(large / (1e20 * (2 * 0.50000000000001 - 1)) - 1) < 1e-12;
            });
            Check("N2 of -1234.5 and -98765432.1 equals double's N2 in every installed culture (NumberNegativePattern)", () =>
            {
                int checkedCultures = 0;
                foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
                {
                    foreach (double d in new[] { -1234.5, -98765432.1 })
                    {
                        string mine = ((BigDouble)d).ToString("N2", culture);
                        string runtime = d.ToString("N2", culture);
                        if (mine != runtime)
                        {
                            s_log.AppendLine("    " + culture.Name + ": BigDouble '" + mine + "', double '" + runtime + "'");
                            return false;
                        }
                        if (!BigDouble.TryParse(mine, culture, out var back) || !back.Equals((BigDouble)d))
                        {
                            s_log.AppendLine("    " + culture.Name + ": '" + mine + "' does not read back");
                            return false;
                        }
                    }
                    checkedCultures++;
                }
                Info("N2 compared in " + checkedCultures + " cultures");
                return checkedCultures > 0;
            });
            Check("culture parser: \"1.2,3\" and \"2.90\" (fr-FR) are rejected like double.Parse", () =>
                !BigDouble.TryParse("1.2,3", CultureInfo.GetCultureInfo("en-US"), out _)
                && !BigDouble.TryParse("2.90", CultureInfo.GetCultureInfo("fr-FR"), out _)
                && BigDouble.Parse("1,234.5", CultureInfo.GetCultureInfo("en-US")) == 1234.5);
            Check("Tanh(+-Infinity) == +-1; Sin/Cos/Tan(NaN) are NaN", () =>
                BigDouble.Tanh(BigDouble.PositiveInfinity) == 1 && BigDouble.Tanh(BigDouble.NegativeInfinity) == -1
                && BigDouble.IsNaN(BigDouble.Sin(BigDouble.NaN)) && BigDouble.IsNaN(BigDouble.Cos(BigDouble.NaN)) && BigDouble.IsNaN(BigDouble.Tan(BigDouble.NaN)));
            Check("ApproximatelyEquals compares values: 1e1000 != 1.0001e1000 at 1e-7", () =>
                !BigDouble.ApproximatelyEquals(BigDouble.Parse("1e1000"), BigDouble.Parse("1.0001e1000"), 1e-7)
                && BigDouble.ApproximatelyEquals(BigDouble.Parse("1e1000"), BigDouble.Parse("1.00000001e1000"), 1e-7));
            Check("ToString: Parse(\"1.23e10000\") shows \"1.23e10000\" (valueOf keeps the JS digits) and round-trips", () =>
                BigDouble.Parse("1.23e10000").ToString() == "1.23e10000" && BigDouble.Parse("1.23e10000").valueOf() == "1.2299999999997036e10000"
                && $"{BigDouble.Parse("-4.56e-2000")}" == "-4.56e-2000" && BigDouble.Parse("1.5e300").ToString() == "1.5e300"
                && BigDouble.Parse(BigDouble.MaxValue.ToString()).Equals(BigDouble.MaxValue));

            // Formatting and parsing at the edges of the double range (Mono throws OverflowException where .NET Core
            // returns Infinity).
            foreach (var extreme in new[] { BigDouble.MaxValue, BigDouble.MinValue, BigDouble.dLayerMin, BigDouble.dLayerSafeMax,
                         new BigDouble(double.MaxValue), new BigDouble(double.Epsilon), BigDouble.FromComponents(1, 1.7976931348623157e308, 20) })
            {
                Check("ToString/ToSaveString/Parse of " + Describe(extreme), () =>
                {
                    string text = extreme.ToString();
                    string save = extreme.ToSaveString();
                    return BigDouble.Parse(save).Equals(extreme) && BigDouble.Parse(text).Equals(extreme);
                });
            }

            // Lossless save strings on this runtime (formatting/parsing of the Mono/IL2CPP class libraries)
            Check("20000 ToSaveString/Parse round trips are exact", () =>
            {
                var random = new System.Random(1);
                for (int i = 0; i < 20000; i++)
                {
                    var v = BigDouble.randomDecimalForTesting(8, random);
                    if (random.NextDouble() < 0.3)
                    {
                        v = v.recip();
                    }
                    if (random.NextDouble() < 0.05)
                    {
                        v = BigDouble.FromComponents(v.sign == 0 ? 1 : v.sign, random.NextDouble() * 1.7e308, 20 + random.NextDouble() * 1e15);
                    }
                    string s = v.ToSaveString();
                    var back = BigDouble.Parse(s);
                    if (!back.Equals(v))
                    {
                        s_log.AppendLine("    first mismatch: " + s + " -> " + back.ToSaveString());
                        return false;
                    }
                }
                return true;
            });
            Check("20000 ToString/Parse round trips are exact", () =>
            {
                var random = new System.Random(2);
                for (int i = 0; i < 20000; i++)
                {
                    var v = BigDouble.randomDecimalForTesting(8, random);
                    if (random.NextDouble() < 0.3)
                    {
                        v = v.recip();
                    }
                    string s = v.ToString();
                    var back = BigDouble.Parse(s);
                    if (!back.Equals(v))
                    {
                        s_log.AppendLine("    first mismatch: " + s + " (" + v.ToSaveString() + ") -> " + back.ToSaveString());
                        return false;
                    }
                }
                return true;
            });

            var values = new[]
            {
                BigDouble.Zero, BigDouble.One, new BigDouble(-123.456), new BigDouble(0.1), new BigDouble(0.1 + 0.2), new BigDouble(1e-10),
                new BigDouble(12345.678901234567), new BigDouble(1.5e300), BigDouble.Pow(1.15, 1234.567), BigDouble.Parse("1e309"),
                BigDouble.Parse("1.5e1000000000"), BigDouble.Parse("-1.5e1000000000000000"), BigDouble.Parse("ee20"),
                BigDouble.Parse("(e^1000)123.5"), BigDouble.Parse("1e-1000"), BigDouble.MaxValue, BigDouble.MinValue,
            };

            // SerializableBigDouble through JsonUtility must be exact (it stores ToSaveString text).
            Check("JsonUtility: SerializableBigDouble fields are exact (" + values.Length + " values, 5 save/load cycles)", () =>
            {
                foreach (var v in values)
                {
                    var current = new BigDoubleSelfTestData { asString = v, array = new BigDouble[0], list = new List<BigDouble>() };
                    for (int cycle = 0; cycle < 5; cycle++)
                    {
                        current = JsonUtility.FromJson<BigDoubleSelfTestData>(JsonUtility.ToJson(current));
                    }
                    if (!((BigDouble)current.asString).Equals(v))
                    {
                        s_log.AppendLine("    mismatch: " + v.ToSaveString() + " -> " + current.asString);
                        return false;
                    }
                }
                return true;
            });

            // INFO: how precisely does JsonUtility write plain doubles?
            try
            {
                var doubles = new[] { 0.1 + 0.2, 1000000000000000.125, 123456789.12345678, Math.PI, 1e-300, 1.7976931348623157e308 };
                int exact = 0;
                var sb = new StringBuilder();
                foreach (var d in doubles)
                {
                    string json = JsonUtility.ToJson(new BigDoubleSelfTestDouble { value = d });
                    double back = JsonUtility.FromJson<BigDoubleSelfTestDouble>(json).value;
                    if (back.Equals(d))
                    {
                        exact++;
                    }
                    sb.Append("  ").Append(json);
                }
                Info("JsonUtility keeps " + exact + " of " + doubles.Length + " double values exactly:" + sb);
            }
            catch (Exception ex)
            {
                Info("JsonUtility double probe threw " + ex.GetType().Name + ": " + ex.Message);
            }

            // INFO: direct BigDouble fields depend on the precision above.
            try
            {
                int changed = 0;
                int approximatelyEqual = 0;
                string example = null;
                foreach (var v in values)
                {
                    var current = new BigDoubleSelfTestData { direct = v, array = new[] { v, -v }, list = new List<BigDouble> { v }, asString = default };
                    for (int cycle = 0; cycle < 5; cycle++)
                    {
                        current = JsonUtility.FromJson<BigDoubleSelfTestData>(JsonUtility.ToJson(current));
                    }
                    if (!current.direct.Equals(v) || !current.array[0].Equals(v) || !current.array[1].Equals(-v) || !current.list[0].Equals(v))
                    {
                        changed++;
                        if (current.direct.eqTolerance(v, 1e-13))
                        {
                            approximatelyEqual++;
                        }
                        example = example ?? v.ToSaveString() + " -> " + current.direct.ToSaveString();
                    }
                }
                Info("JsonUtility with direct BigDouble fields: " + changed + " of " + values.Length + " values changed (" +
                     approximatelyEqual + " of them within 1e-13 relative)" + (example != null ? ", e.g. " + example : "") +
                     ". Use SerializableBigDouble for save files.");
                Info("example JSON: " + JsonUtility.ToJson(new BigDoubleSelfTestData { direct = values[10], asString = values[10], array = new BigDouble[0], list = new List<BigDouble>() }));
            }
            catch (Exception ex)
            {
                Info("JsonUtility with direct BigDouble fields threw " + ex.GetType().Name + ": " + ex.Message);
            }

            // NaN/Infinity through JsonUtility (JSON has no NaN/Infinity literals)
            foreach (var special in new[] { BigDouble.NaN, BigDouble.PositiveInfinity, BigDouble.NegativeInfinity })
            {
                Check(special + " survives as SerializableBigDouble", () =>
                {
                    var data = new BigDoubleSelfTestData { direct = special, asString = special, array = new BigDouble[0], list = new List<BigDouble>() };
                    string json = JsonUtility.ToJson(data);
                    var back = JsonUtility.FromJson<BigDoubleSelfTestData>(json);
                    Info(special + " as direct BigDouble field -> " + back.direct + "   json: " + json);
                    return ((BigDouble)back.asString).Equals(special);
                });
            }

            // INFO: BigDouble's F/E/N formats decide by the exact stored value; how does this runtime format doubles?
            try
            {
                var random = new System.Random(2);
                int compared = 0;
                int different = 0;
                string example = "";
                foreach (string format in new[] { "F2", "E3", "N2" })
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        double d = Math.Round((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-2, 12)), random.Next(0, 5));
                        string mine = ((BigDouble)d).ToString(format, CultureInfo.InvariantCulture);
                        string runtime = d.ToString(format, CultureInfo.InvariantCulture);
                        compared++;
                        if (mine != runtime)
                        {
                            different++;
                            if (example.Length == 0)
                            {
                                example = " (e.g. " + d.ToString("R", CultureInfo.InvariantCulture) + " " + format + ": BigDouble " + mine + ", double " + runtime + ")";
                            }
                        }
                    }
                }
                Info("standard formats: " + different + " of " + compared + " texts differ from this runtime's double formatting" + example);
            }
            catch (Exception ex)
            {
                Info("format comparison threw " + ex.GetType().Name + ": " + ex.Message);
            }

            // INFO: does JsonUtility keep field initializers for fields missing in the JSON?
            try
            {
                var loaded = JsonUtility.FromJson<BigDoubleSelfTestDefaults>("{\"marker\":5}");
                Info("JsonUtility.FromJson keeps the initializer of a missing SerializableBigDouble field: " +
                     (loaded.multiplier.SerializedValue == ((SerializableBigDouble)1).SerializedValue) + " (value '" + loaded.multiplier.SerializedValue + "')");
            }
            catch (Exception ex)
            {
                Info("JsonUtility initializer probe threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string Describe(BigDouble v)
        {
            return "(" + v.sign.ToString("R", CultureInfo.InvariantCulture) + ", " + v.layer.ToString("R", CultureInfo.InvariantCulture) +
                   ", " + v.mag.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        private static void Check(string name, Func<bool> test)
        {
            bool ok;
            string error = null;
            try
            {
                ok = test();
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.GetType().Name + ": " + ex.Message;
            }
            if (ok)
            {
                s_passed++;
                s_log.AppendLine("  PASS " + name);
            }
            else
            {
                s_failed++;
                s_log.AppendLine("  FAIL " + name + (error != null ? " -> " + error : ""));
            }
        }

        private static void Info(string message)
        {
            s_log.AppendLine("  INFO " + message);
        }
    }
}
#endif
