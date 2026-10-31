// BigDouble - C# port of break_eternity.js 2.1.3 (https://github.com/Patashu/break_eternity.js, MIT license)
// for Unity 6 / C# 9 / .NET Standard 2.0+.
//
// Sections of this file:
//   1. BigDouble core: storage, constants, construction, normalization, arithmetic, logarithms, powers, trig
//   2. Operators, conversions, IComparable/IEquatable and the C#-style static API (BigDouble.Pow ...)
//   3. Parsing (Parse/TryParse) and string conversion (ToString, ToSaveString, toFixed ...)
//   4. Hyperoperators: tetration, super-logarithm, layeradd, Lambert W, super-roots, pentation
//   5. Lowercase static aliases from break_eternity.js and the previous C# port
//   6. SerializableBigDouble: string wrapper for save files
// LRUCache.cs (cache used by Parse) is required as well.
//
// The value of a BigDouble is sign * 10^10^...^10^mag with 'layer' tens.
// If the layer is not 0, a negative mag means the reciprocal of the corresponding number with positive mag.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace BreakEternity
{
    /// <summary>
    /// A number that can represent values up to 10^^1.79e308 (and down to its reciprocal) with roughly the
    /// precision of a <see cref="double"/>. Behaves like an immutable value type: every operation returns a new value.
    /// </summary>
    [Serializable]
    [DebuggerDisplay("{ToString(),nq}")]
    [TypeConverter(typeof(BigDoubleTypeConverter))] // Newtonsoft Json.NET stores it as its save text through this
#if NETCOREAPP3_0_OR_GREATER
    [System.Text.Json.Serialization.JsonConverter(typeof(BigDoubleJsonConverter))]
#endif
    public struct BigDouble : IComparable, IComparable<BigDouble>, IEquatable<BigDouble>, IFormattable, IXmlSerializable
    {
        // NOTE: lookup tables and the parse cache live in nested static classes, so they are initialized on first use,
        // independent of the order of the static fields below.

        private const double MAX_SIGNIFICANT_DIGITS = 17; //Maximum number of digits of precision to assume in a double
        private const double EXP_LIMIT = 9e15; //If we're ABOVE this value, increase a layer. (9e15 is close to the largest integer that can fit in a double.)
        private const double LAYER_DOWN = 15.954242509439325; // == Math.Log10(9e15), verified by the test suite
        internal static double LAYER_DOWN_ForTests => LAYER_DOWN;
        private const double FIRST_NEG_LAYER = 1 / 9e15; //At layer 0, smaller non-zero numbers than this become layer 1 numbers with negative mag.
        private const int NUMBER_EXP_MAX = 308; //The largest exponent that can appear in a double, though not all mantissas are valid here.
        private const int NUMBER_EXP_MIN = -324; //The smallest exponent that can appear in a double, though not all mantissas are valid here.
        private const int MAX_ES_IN_A_ROW = 5; //For default ToString behaviour, when to swap from eee... to (e^n) syntax.
        private const double MAX_SAFE_INTEGER = 9007199254740991;
        private const double OMEGA = 0.56714329040978387299997; // W(1, 0)
        private const double EXPN1 = 0.36787944117144232159553; // exp(-1)
        private const double TETRATION_CONVERGENCE_MAX = 1.44466786100976613366; // e^(1/e)
        private const double TETRATION_CONVERGENCE_MIN = 0.06598803584531253708; // e^-e

        #region Storage

        // Serialized by Unity (Inspector, JsonUtility). Do not rename: existing saves/scenes reference these names.
#if UNITY_5_3_OR_NEWER
        [UnityEngine.SerializeField]
#endif
        private double _sign;
#if UNITY_5_3_OR_NEWER
        [UnityEngine.SerializeField]
#endif
        private double _layer;
#if UNITY_5_3_OR_NEWER
        [UnityEngine.SerializeField]
#endif
        private double _mag;

        /// <summary>-1, 0 or 1 (NaN for NaN values).</summary>
        public readonly double sign => _sign;

        /// <summary>Number of times 10 is raised to the power of <see cref="mag"/>. 0 for ordinary numbers.</summary>
        public readonly double layer => _layer;

        /// <summary>Magnitude at the current layer.</summary>
        public readonly double mag => _mag;

        private BigDouble(double sign, double layer, double mag)
        {
            _sign = sign;
            _layer = layer;
            _mag = mag;
        }

        /// <summary>
        /// Creates a BigDouble from a double. Doubles with an absolute value from 1.1e-16 up to 9e15 (and 0) are
        /// stored exactly; outside that range the value is stored as its base-10 logarithm (~15 significant digits).
        /// </summary>
        public BigDouble(double value)
        {
            this = fromNumber(value);
        }

        /// <summary>Parses a string (see <see cref="Parse(string)"/>). Throws <see cref="FormatException"/> on invalid input.</summary>
        public BigDouble(string value)
        {
            this = Parse(value);
        }

        /// <summary>Copies a BigDouble.</summary>
        public BigDouble(BigDouble value)
        {
            this = value;
        }

        // XmlSerializer sees no public settable members and would silently write an empty element (read back as 0).
        XmlSchema IXmlSerializable.GetSchema()
        {
            return null;
        }

        void IXmlSerializable.ReadXml(XmlReader reader)
        {
            this = Parse(reader.ReadElementContentAsString());
        }

        void IXmlSerializable.WriteXml(XmlWriter writer)
        {
            writer.WriteString(ToSaveString());
        }

        #endregion

        #region Constants

        /// <summary>Represents the number 0.</summary>
        public static readonly BigDouble dZero = new BigDouble(0, 0, 0);
        /// <summary>Represents the number 1.</summary>
        public static readonly BigDouble dOne = new BigDouble(1, 0, 1);
        /// <summary>Represents the number -1.</summary>
        public static readonly BigDouble dNegOne = new BigDouble(-1, 0, 1);
        /// <summary>Represents the number 2.</summary>
        public static readonly BigDouble dTwo = new BigDouble(1, 0, 2);
        /// <summary>Represents the number 10.</summary>
        public static readonly BigDouble dTen = new BigDouble(1, 0, 10);
        /// <summary>Represents a NaN (Not A Number) value.</summary>
        public static readonly BigDouble dNaN = new BigDouble(double.NaN, double.NaN, double.NaN);
        /// <summary>Represents positive infinity.</summary>
        public static readonly BigDouble dInf = new BigDouble(1, double.PositiveInfinity, double.PositiveInfinity);
        /// <summary>Represents negative infinity.</summary>
        public static readonly BigDouble dNegInf = new BigDouble(-1, double.PositiveInfinity, double.PositiveInfinity);
        /// <summary>The largest value a double can have, approximately 1.79e308.</summary>
        public static readonly BigDouble dNumberMax = FC(1, 0, double.MaxValue);
        /// <summary>The smallest positive value a double can have, approximately 5e-324.</summary>
        public static readonly BigDouble dNumberMin = FC(1, 0, double.Epsilon);
        /// <summary>Largest BigDouble where adding 1 to the layer is a safe operation. Approximately 10^^(9.007e15).</summary>
        public static readonly BigDouble dLayerSafeMax = FC(1, MAX_SAFE_INTEGER, EXP_LIMIT - 1);
        /// <summary>Smallest BigDouble where adding 1 to the layer is a safe operation. Approximately 1 / (10^^(9.007e15)).</summary>
        public static readonly BigDouble dLayerSafeMin = FC(1, MAX_SAFE_INTEGER, -(EXP_LIMIT - 1));
        /// <summary>The largest finite BigDouble. Approximately 10^^(1.79e308).</summary>
        public static readonly BigDouble dLayerMax = FC(1, double.MaxValue, EXP_LIMIT - 1);
        /// <summary>The smallest positive BigDouble. Approximately 1 / (10^^(1.79e308)).</summary>
        public static readonly BigDouble dLayerMin = FC(1, double.MaxValue, -(EXP_LIMIT - 1));

        /// <summary>0</summary>
        public static readonly BigDouble Zero = dZero;
        /// <summary>1</summary>
        public static readonly BigDouble One = dOne;
        /// <summary>-1</summary>
        public static readonly BigDouble MinusOne = dNegOne;
        /// <summary>2</summary>
        public static readonly BigDouble Two = dTwo;
        /// <summary>10</summary>
        public static readonly BigDouble Ten = dTen;
        /// <summary>Not a number.</summary>
        public static readonly BigDouble NaN = dNaN;
        /// <summary>Positive infinity.</summary>
        public static readonly BigDouble PositiveInfinity = dInf;
        /// <summary>Negative infinity.</summary>
        public static readonly BigDouble NegativeInfinity = dNegInf;
        /// <summary>Largest finite value (≈ 10^^1.79e308).</summary>
        public static readonly BigDouble MaxValue = dLayerMax;
        /// <summary>Most negative finite value (≈ -10^^1.79e308).</summary>
        public static readonly BigDouble MinValue = FC(-1, double.MaxValue, EXP_LIMIT - 1);

        // Constants that break_eternity.js creates from strings at call time ("1e-300", "eee15", "1ee-16", "1e18").
        private static readonly BigDouble s_1em300 = fromNumber(1e-300);
        private static readonly BigDouble s_eee15 = FC(1, 3, 15);
        private static readonly BigDouble s_1eem16 = FC(1, 2, -16);
        private static readonly BigDouble s_1e18 = fromNumber(1e18);

        #endregion

        #region Small helpers

        private static BigDouble FC(double sign, double layer, double mag)
        {
            var result = new BigDouble(sign, layer, mag);
            result.NormalizeInPlace();
            return result;
        }

        private static BigDouble FC_NN(double sign, double layer, double mag)
        {
            return new BigDouble(sign, layer, mag);
        }

        /// <summary>Equivalent of JavaScript's Number.isFinite.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFiniteD(double x)
        {
            return Math.Abs(x) <= double.MaxValue; // false for NaN and +-Infinity
        }

        /// <summary>Equivalent of JavaScript's Math.sign: keeps NaN and signed zero. (Math.Sign throws for NaN.)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double JsSign(double x)
        {
            return x > 0 ? 1 : x < 0 ? -1 : x;
        }

        /// <summary>Equivalent of JavaScript's Math.round (ties are rounded towards +Infinity).</summary>
        private static double JsRound(double x)
        {
            double r = Math.Floor(x);
            return x - r >= 0.5 ? r + 1 : r;
        }

        private static double f_maglog10(double n)
        {
            return JsSign(n) * Math.Log10(Math.Abs(n));
        }

        private static class PowersOf10
        {
            // Math.Pow(10, exponent) is slightly inaccurate for large exponents, so use a lookup table.
            private static readonly double[] s_table = Create();
            private const int IndexOf0 = -NUMBER_EXP_MIN - 1; // 323

            private static double[] Create()
            {
                var table = new double[NUMBER_EXP_MAX - NUMBER_EXP_MIN];
                for (int i = 0; i < table.Length; i++)
                {
                    table[i] = double.Parse("1e" + (i - IndexOf0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
                }
                return table;
            }

            public static double Get(double power)
            {
                int index = (int)power + IndexOf0;
                if (power != Math.Floor(power) || index < 0 || index >= s_table.Length)
                {
                    return Math.Pow(10, power);
                }
                return s_table[index];
            }
        }

        #endregion

        #region Normalization and factories

        /// <summary>
        /// Turns the components into a valid BigDouble. (break_eternity.js normalize())
        /// Any 0 is totally zero (0, 0, 0) and any NaN is totally NaN (NaN, NaN, NaN).
        /// Anything layer 0 has mag 0 OR mag &gt;= 1/9e15 and &lt; 9e15.
        /// Anything layer 1 or higher has abs(mag) &gt;= 15.954 and &lt; 9e15.
        /// Any positive infinity is (1, Infinity, Infinity) and any negative infinity is (-1, Infinity, Infinity).
        /// </summary>
        private void NormalizeInPlace()
        {
            //Any 0 is totally 0
            if (_sign == 0 || (_mag == 0 && _layer == 0) || (_mag == double.NegativeInfinity && _layer > 0 && IsFiniteD(_layer)))
            {
                _sign = 0;
                _mag = 0;
                _layer = 0;
                return;
            }

            //extract sign from negative mag at layer 0
            if (_layer == 0 && _mag < 0)
            {
                _mag = -_mag;
                _sign = -_sign;
            }

            //Handle infinities
            if (double.IsInfinity(_mag) || double.IsInfinity(_layer))
            {
                if (double.IsNaN(_sign) || double.IsNaN(_mag) || double.IsNaN(_layer))
                {
                    _sign = double.NaN;
                    _layer = double.NaN;
                    _mag = double.NaN;
                    return;
                }
                _sign = _sign < 0 ? -1 : 1;
                _mag = double.PositiveInfinity;
                _layer = double.PositiveInfinity;
                return;
            }

            //Handle shifting from layer 0 to negative layers.
            if (_layer == 0 && _mag < FIRST_NEG_LAYER)
            {
                _layer += 1;
                _mag = Math.Log10(_mag);
                return;
            }

            double absmag = Math.Abs(_mag);
            double signmag = JsSign(_mag);

            if (absmag >= EXP_LIMIT)
            {
                _layer += 1;
                _mag = signmag * Math.Log10(absmag);
                return;
            }

            while (absmag < LAYER_DOWN && _layer > 0)
            {
                _layer -= 1;
                if (_layer == 0)
                {
                    _mag = Math.Pow(10, _mag);
                }
                else
                {
                    _mag = signmag * Math.Pow(10, absmag);
                    absmag = Math.Abs(_mag);
                    signmag = JsSign(_mag);
                }
            }

            if (_layer == 0)
            {
                if (_mag < 0)
                {
                    //extract sign from negative mag at layer 0
                    _mag = -_mag;
                    _sign = -_sign;
                }
                else if (_mag == 0)
                {
                    //excessive rounding can give us all zeroes
                    _sign = 0;
                }
            }

            if (double.IsNaN(_sign) || double.IsNaN(_layer) || double.IsNaN(_mag))
            {
                _sign = double.NaN;
                _layer = double.NaN;
                _mag = double.NaN;
            }
        }

        /// <summary>Returns a normalized copy. Values produced by this library are always normalized already.</summary>
        public readonly BigDouble normalize()
        {
            var copy = this;
            copy.NormalizeInPlace();
            return copy;
        }

        /// <summary>Turns the given components into a valid BigDouble.</summary>
        public static BigDouble fromComponents(double sign, double layer, double mag)
        {
            return FC(sign, layer, mag);
        }

        /// <summary>
        /// Turns the given components into a BigDouble without normalizing them. Only valid if the components
        /// already form a valid BigDouble. Game code should not need this.
        /// </summary>
        public static BigDouble fromComponents_noNormalize(double sign, double layer, double mag)
        {
            return FC_NN(sign, layer, mag);
        }

        /// <summary>Returns mantissa * 10^exponent. Works for exponents far beyond the double range.</summary>
        public static BigDouble fromMantissaExponent(double mantissa, double exponent)
        {
            return FC(JsSign(mantissa), 1, exponent + Math.Log10(Math.Abs(mantissa)));
        }

        /// <summary>Same as <see cref="fromMantissaExponent"/> (kept for break_eternity.js compatibility).</summary>
        public static BigDouble fromMantissaExponent_noNormalize(double mantissa, double exponent)
        {
            return fromMantissaExponent(mantissa, exponent);
        }

        /// <summary>Converts a double into a BigDouble (exact for |value| in [1.1e-16, 9e15), see the constructor).</summary>
        public static BigDouble fromNumber(double value)
        {
            // Fast paths for ordinary numbers; identical to the general normalization result.
            if (value > FIRST_NEG_LAYER && value < EXP_LIMIT)
            {
                return new BigDouble(1, 0, value);
            }
            if (value < -FIRST_NEG_LAYER && value > -EXP_LIMIT)
            {
                return new BigDouble(-1, 0, -value);
            }
            return FC(JsSign(value), 0, Math.Abs(value));
        }

        // fromNumber without the fast paths (only used by the benchmarks to measure them).
        internal static BigDouble FromNumberWithoutFastPath(double value)
        {
            return FC(JsSign(value), 0, Math.Abs(value));
        }

        /// <summary>Creates a copy of the provided value.</summary>
        public static BigDouble fromDecimal(BigDouble value)
        {
            return value;
        }

        #endregion

        #region Mantissa / exponent view

        private readonly void GetMantissaExponent(out double mantissa, out double exponent)
        {
            if (_sign == 0)
            {
                mantissa = 0;
                exponent = 0;
                return;
            }
            if (_layer == 0)
            {
                double exp = Math.Floor(Math.Log10(_mag));
                double man;
                //handle special case 5e-324
                if (_mag == 5e-324)
                {
                    man = 5;
                }
                else
                {
                    man = _mag / PowersOf10.Get(exp);
                    // Math.Log10 may be off by one ulp around exact powers of ten; keep the mantissa in [1, 10).
                    if (man >= 10)
                    {
                        exp += 1;
                        man = _mag / PowersOf10.Get(exp);
                    }
                    else if (man < 1)
                    {
                        exp -= 1;
                        man = _mag / PowersOf10.Get(exp);
                    }
                }
                mantissa = _sign * man;
                exponent = exp;
                return;
            }
            if (_layer == 1)
            {
                double residue = _mag - Math.Floor(_mag);
                mantissa = _sign * Math.Pow(10, residue);
                exponent = Math.Floor(_mag);
                return;
            }
            //mantissa stops being relevant past 1e9e15 / ee15.954
            mantissa = _sign;
            if (_layer == 2)
            {
                exponent = Math.Floor(JsSign(_mag) * Math.Pow(10, Math.Abs(_mag)));
            }
            else
            {
                exponent = _mag * double.PositiveInfinity;
            }
        }

        /// <summary>Mantissa in scientific notation (in [1, 10) for layer 0 and 1, otherwise just the sign).</summary>
        public readonly double m
        {
            get
            {
                GetMantissaExponent(out double man, out _);
                return man;
            }
        }

        /// <summary>Exponent in scientific notation (Infinity for layer 3 and above).</summary>
        public readonly double e
        {
            get
            {
                GetMantissaExponent(out _, out double exp);
                return exp;
            }
        }

        /// <summary>Same as <see cref="sign"/>.</summary>
        public readonly double s => _sign;

        /// <summary>Same as <see cref="m"/>.</summary>
        public readonly double mantissa => m;

        /// <summary>Same as <see cref="e"/>.</summary>
        public readonly double exponent => e;

        #endregion

        #region Conversion to double

        /// <summary>
        /// Converts to a double. Returns +-Infinity for values beyond ±1.79e308 and 0 for values too close to zero.
        /// </summary>
        public readonly double toNumber()
        {
            if (double.IsPositiveInfinity(_mag) && double.IsPositiveInfinity(_layer))
            {
                if (_sign == 1)
                {
                    return double.PositiveInfinity;
                }
                if (_sign == -1)
                {
                    return double.NegativeInfinity;
                }
            }
            if (!IsFiniteD(_layer))
            {
                return double.NaN;
            }
            if (_layer == 0)
            {
                return _sign * _mag;
            }
            if (_layer == 1)
            {
                return _sign * Math.Pow(10, _mag);
            }
            //overflow for any normalized BigDouble
            if (double.IsNaN(_mag) || double.IsNaN(_sign))
            {
                return double.NaN;
            }
            return _mag > 0 ? (_sign > 0 ? double.PositiveInfinity : double.NegativeInfinity) : 0;
        }

        #endregion

        #region Sign, rounding

        /// <summary>Absolute value.</summary>
        public readonly BigDouble abs()
        {
            if (double.IsNaN(_sign))
            {
                return dNaN;
            }
            return FC_NN(_sign == 0 ? 0 : 1, _layer, _mag);
        }

        /// <summary>Negation (-x).</summary>
        public readonly BigDouble neg()
        {
            return _sign == 0 ? dZero : FC_NN(-_sign, _layer, _mag);
        }

        /// <summary>Same as <see cref="neg()"/>.</summary>
        public readonly BigDouble negate()
        {
            return neg();
        }

        /// <summary>Same as <see cref="neg()"/>.</summary>
        public readonly BigDouble negated()
        {
            return neg();
        }

        /// <summary>Returns the sign (-1, 0, 1 or NaN).</summary>
        public readonly double sgn()
        {
            return _sign;
        }

        /// <summary>Returns the sign as a BigDouble (-1, 0, 1 or NaN; name of the previous C# port).</summary>
        public readonly BigDouble signum()
        {
            return fromNumber(_sign);
        }

        /// <summary>Rounds to the nearest integer; halves are rounded away from zero (like break_eternity.js).</summary>
        public readonly BigDouble round()
        {
            if (_mag < 0)
            {
                return dZero;
            }
            if (_layer == 0)
            {
                return FC(_sign, 0, JsRound(_mag));
            }
            return this;
        }

        /// <summary>Largest integer less than or equal to this value.</summary>
        public readonly BigDouble floor()
        {
            if (_mag < 0)
            {
                return _sign == -1 ? dNegOne : dZero;
            }
            if (_sign == -1)
            {
                return neg().ceil().neg();
            }
            if (_layer == 0)
            {
                return FC(_sign, 0, Math.Floor(_mag));
            }
            return this;
        }

        /// <summary>Smallest integer greater than or equal to this value.</summary>
        public readonly BigDouble ceil()
        {
            if (_mag < 0)
            {
                //The ceiling of something tiny like 10^10^-100 is 1, since it is still greater than 0
                return _sign == 1 ? dOne : dZero;
            }
            if (_sign == -1)
            {
                return neg().floor().neg();
            }
            if (_layer == 0)
            {
                return FC(_sign, 0, Math.Ceiling(_mag));
            }
            return this;
        }

        /// <summary>Integer part (rounds towards zero).</summary>
        public readonly BigDouble trunc()
        {
            if (_mag < 0)
            {
                return dZero;
            }
            if (_layer == 0)
            {
                return FC(_sign, 0, Math.Truncate(_mag));
            }
            return this;
        }

        #endregion

        #region Addition, multiplication, division, modulo

        /// <summary>Addition.</summary>
        public readonly BigDouble add(BigDouble value)
        {
            // Fast path for two ordinary numbers. Equivalent to the general path below (IEEE addition is
            // commutative, x + 0 == x and x + (-x) == 0), just without the special case checks.
            if (_layer == 0 && value._layer == 0)
            {
                return fromNumber(_sign * _mag + value._sign * value._mag);
            }

            //inf/nan check
            if (!IsFiniteD(_layer) || !IsFiniteD(value._layer))
            {
                return AddNonFinite(this, value);
            }

            //Special case - if one of the numbers is 0, return the other number.
            if (_sign == 0)
            {
                return value;
            }
            if (value._sign == 0)
            {
                return this;
            }

            //Special case - Adding a number to its negation produces 0, no matter how large.
            if (_sign == -value._sign && _layer == value._layer && _mag == value._mag)
            {
                return dZero;
            }

            //Special case: If one of the numbers is layer 2 or higher, just take the bigger number.
            if (_layer >= 2 || value._layer >= 2)
            {
                return maxabs(value);
            }

            BigDouble a, b;
            if (cmpabs(value) > 0)
            {
                a = this;
                b = value;
            }
            else
            {
                a = value;
                b = this;
            }

            double layera = a._layer * JsSign(a._mag);
            double layerb = b._layer * JsSign(b._mag);

            //If one of the numbers is 2+ layers higher than the other, just take the bigger number.
            if (layera - layerb >= 2)
            {
                return a;
            }

            double magdiff, mantissa;
            if (layera == 0 && layerb == -1)
            {
                if (Math.Abs(b._mag - Math.Log10(a._mag)) > MAX_SIGNIFICANT_DIGITS)
                {
                    return a;
                }
                // An ordinary number and a tiny one: plain addition. The logarithmic formula of break_eternity.js cost the
                // ordinary number its last 1-2 digits (1.0665729409431532e-11 + 7.6e-28 became ...575e-11).
                return fromNumber(a._sign * a._mag + b._sign * Math.Pow(10, b._mag));
            }

            if (layera == 1 && layerb == 0)
            {
                if (Math.Abs(a._mag - Math.Log10(b._mag)) > MAX_SIGNIFICANT_DIGITS)
                {
                    return a;
                }
                magdiff = Math.Pow(10, a._mag - Math.Log10(b._mag));
                mantissa = b._sign + a._sign * magdiff;
                return FC(JsSign(mantissa), 1, Math.Log10(b._mag) + Math.Log10(Math.Abs(mantissa)));
            }

            if (Math.Abs(a._mag - b._mag) > MAX_SIGNIFICANT_DIGITS)
            {
                return a;
            }
            double logRatio = a._mag - b._mag;
            // Nearly equal values of opposite sign: 10^d - 1 directly, 10^d rounds to 1 + a few ulps there.
            mantissa = a._sign != b._sign && logRatio < 0.15
                ? a._sign * Expm1(logRatio * 2.302585092994046)
                : b._sign + a._sign * Math.Pow(10, logRatio);
            return FC(JsSign(mantissa), 1, b._mag + Math.Log10(Math.Abs(mantissa)));
        }

        private static BigDouble AddNonFinite(BigDouble a, BigDouble b)
        {
            if (a.isNan() || b.isNan())
            {
                return dNaN;
            }
            bool aInf = !IsFiniteD(a._layer);
            bool bInf = !IsFiniteD(b._layer);
            //Infinity + -Infinity = NaN
            if (aInf && bInf && a._sign != b._sign)
            {
                return dNaN;
            }
            return aInf ? a : b;
        }

        /// <summary>Same as <see cref="add(BigDouble)"/>.</summary>
        public readonly BigDouble plus(BigDouble value)
        {
            return add(value);
        }

        /// <summary>Subtraction.</summary>
        public readonly BigDouble sub(BigDouble value)
        {
            return add(value.neg());
        }

        /// <summary>Same as <see cref="sub(BigDouble)"/>.</summary>
        public readonly BigDouble subtract(BigDouble value)
        {
            return sub(value);
        }

        /// <summary>Same as <see cref="sub(BigDouble)"/>.</summary>
        public readonly BigDouble minus(BigDouble value)
        {
            return sub(value);
        }

        /// <summary>Multiplication.</summary>
        public readonly BigDouble mul(BigDouble value)
        {
            // Fast path for two ordinary numbers; identical result to the general path below.
            if (_layer == 0 && value._layer == 0)
            {
                return fromNumber(_sign * value._sign * _mag * value._mag);
            }

            //inf/nan check
            if (!IsFiniteD(_layer) || !IsFiniteD(value._layer))
            {
                return MulNonFinite(this, value);
            }

            //Special case - if one of the numbers is 0, return 0.
            if (_sign == 0 || value._sign == 0)
            {
                return dZero;
            }

            //Special case - Multiplying a number by its own reciprocal yields +/- 1, no matter how large.
            if (_layer == value._layer && _mag == -value._mag)
            {
                return FC_NN(_sign * value._sign, 0, 1);
            }

            BigDouble a, b;
            //Which number is bigger in terms of its multiplicative distance from 1?
            if (_layer > value._layer || (_layer == value._layer && Math.Abs(_mag) > Math.Abs(value._mag)))
            {
                a = this;
                b = value;
            }
            else
            {
                a = value;
                b = this;
            }

            //Special case: If one of the numbers is layer 3 or higher or one of the numbers is 2+ layers bigger than the other, just take the bigger number.
            if (a._layer >= 3 || a._layer - b._layer >= 2)
            {
                return FC(a._sign * b._sign, a._layer, a._mag);
            }

            if (a._layer == 1 && b._layer == 0)
            {
                return FC(a._sign * b._sign, 1, a._mag + Math.Log10(b._mag));
            }

            if (a._layer == 1 && b._layer == 1)
            {
                return FC(a._sign * b._sign, 1, a._mag + b._mag);
            }

            if (a._layer == 2 && (b._layer == 1 || b._layer == 2))
            {
                var newmag = FC(JsSign(a._mag), a._layer - 1, Math.Abs(a._mag))
                    .add(FC(JsSign(b._mag), b._layer - 1, Math.Abs(b._mag)));
                return FC(a._sign * b._sign, newmag._layer + 1, newmag._sign * newmag._mag);
            }

            // Only reachable with non-normalized input (e.g. fractional layers). break_eternity.js throws here.
            return dNaN;
        }

        private static BigDouble MulNonFinite(BigDouble a, BigDouble b)
        {
            if (a.isNan() || b.isNan())
            {
                return dNaN;
            }
            //Infinity * 0 = NaN
            if (a._sign == 0 || b._sign == 0)
            {
                return dNaN;
            }
            return a._sign * b._sign > 0 ? dInf : dNegInf;
        }

        /// <summary>Same as <see cref="mul(BigDouble)"/>.</summary>
        public readonly BigDouble multiply(BigDouble value)
        {
            return mul(value);
        }

        /// <summary>Same as <see cref="mul(BigDouble)"/>.</summary>
        public readonly BigDouble times(BigDouble value)
        {
            return mul(value);
        }

        /// <summary>Division. Division by zero returns NaN (break_eternity.js behaviour).</summary>
        public readonly BigDouble div(BigDouble value)
        {
            // For two ordinary (layer 0) numbers divide directly: this is correctly rounded like double division,
            // while mul(recip()) can be off by one ulp (e.g. -5 / 3). The quotient cannot overflow or underflow here.
            if (_layer == 0 && value._layer == 0 && value._sign != 0)
            {
                return fromNumber(_sign * _mag / (value._sign * value._mag));
            }
            return mul(value.recip());
        }

        /// <summary>Same as <see cref="div(BigDouble)"/>.</summary>
        public readonly BigDouble divide(BigDouble value)
        {
            return div(value);
        }

        /// <summary>Same as <see cref="div(BigDouble)"/>.</summary>
        public readonly BigDouble divideBy(BigDouble value)
        {
            return div(value);
        }

        /// <summary>Same as <see cref="div(BigDouble)"/>.</summary>
        public readonly BigDouble dividedBy(BigDouble value)
        {
            return div(value);
        }

        /// <summary>Reciprocal (1 / x). The reciprocal of 0 is NaN.</summary>
        public readonly BigDouble recip()
        {
            if (_mag == 0)
            {
                return dNaN;
            }
            if (double.IsPositiveInfinity(_mag))
            {
                return dZero;
            }
            if (_layer == 0)
            {
                return FC(_sign, 0, 1 / _mag);
            }
            return FC(_sign, _layer, -_mag);
        }

        /// <summary>Same as <see cref="recip()"/>.</summary>
        public readonly BigDouble reciprocal()
        {
            return recip();
        }

        /// <summary>Same as <see cref="recip()"/>.</summary>
        public readonly BigDouble reciprocate()
        {
            return recip();
        }

        /// <summary>
        /// Remainder of this / value. Uses truncated division (same as C#'s % on doubles) unless
        /// <paramref name="floored"/> is true (result takes the sign of the divisor). x mod 0 returns 0.
        /// </summary>
        public readonly BigDouble mod(BigDouble value, bool floored = false)
        {
            var vd = value;
            var dec = vd.abs();

            if (isNan() || vd.isNan())
            {
                return dNaN;
            }
            if (eq(dZero) || dec.eq(dZero))
            {
                return dZero;
            }
            if (floored)
            {
                var absmod = abs().mod(dec);
                // fix: the JS version returned |value| instead of 0 for exact multiples with different signs
                if (absmod._sign != 0 && (_sign == -1) != (vd._sign == -1))
                {
                    absmod = vd.abs().sub(absmod);
                }
                return absmod.mul(vd._sign);
            }
            double num_this = toNumber();
            double num_decimal = dec.toNumber();
            //Special case: To avoid precision issues, if both numbers are valid doubles, just call % on those
            if (IsFiniteD(num_this) && IsFiniteD(num_decimal) && num_this != 0 && num_decimal != 0)
            {
                return fromNumber(num_this % num_decimal);
            }
            if (sub(dec).eq(this))
            {
                //decimal is too small to register to this
                return dZero;
            }
            if (dec.sub(this).eq(dec))
            {
                //this is too small to register to decimal
                return this;
            }
            if (_sign == -1)
            {
                return abs().mod(dec).neg();
            }
            return sub(div(dec).floor().mul(dec));
        }

        /// <summary>Same as <see cref="mod(BigDouble, bool)"/>.</summary>
        public readonly BigDouble modulo(BigDouble value, bool floored = false)
        {
            return mod(value, floored);
        }

        /// <summary>Same as <see cref="mod(BigDouble, bool)"/>.</summary>
        public readonly BigDouble modular(BigDouble value, bool floored = false)
        {
            return mod(value, floored);
        }

        #endregion

        #region Comparison

        /// <summary>-1, 0, 1, or 2 if the comparison is unordered (NaN involved).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly int CmpRaw(in BigDouble value)
        {
            if (_sign > value._sign)
            {
                return 1;
            }
            if (_sign < value._sign)
            {
                return -1;
            }
            if (_sign != value._sign)
            {
                return 2; // NaN sign
            }
            int c = CmpAbsRaw(value);
            if (c == 2)
            {
                return 2;
            }
            return _sign > 0 ? c : _sign < 0 ? -c : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly int CmpAbsRaw(in BigDouble value)
        {
            double layera = _mag > 0 ? _layer : -_layer;
            double layerb = value._mag > 0 ? value._layer : -value._layer;
            if (layera > layerb)
            {
                return 1;
            }
            if (layera < layerb)
            {
                return -1;
            }
            if (_mag > value._mag)
            {
                return 1;
            }
            if (_mag < value._mag)
            {
                return -1;
            }
            if (_mag == value._mag && layera == layerb)
            {
                return 0;
            }
            return 2;
        }

        /// <summary>
        /// Returns 1 if this &gt; value, -1 if this &lt; value, 0 if equal. NaN sorts below every other value
        /// (same total order as <see cref="double.CompareTo(double)"/>).
        /// </summary>
        public readonly int cmp(BigDouble value)
        {
            int c = CmpRaw(value);
            if (c != 2)
            {
                return c;
            }
            bool thisNan = isNan();
            bool otherNan = value.isNan();
            if (thisNan)
            {
                return otherNan ? 0 : -1;
            }
            return otherNan ? 1 : 0;
        }

        /// <summary>Compares the absolute values: 1 if |this| &gt; |value|, -1 if smaller, 0 if equal.</summary>
        public readonly int cmpabs(BigDouble value)
        {
            int c = CmpAbsRaw(value);
            return c == 2 ? 0 : c;
        }

        /// <summary>Same as <see cref="cmp(BigDouble)"/>.</summary>
        public readonly int compare(BigDouble value)
        {
            return cmp(value);
        }

        /// <summary>True if this is NaN.</summary>
        public readonly bool isNan()
        {
            return double.IsNaN(_sign) || double.IsNaN(_layer) || double.IsNaN(_mag);
        }

        /// <summary>True if this is finite (by BigDouble standards: 10^^1e100 is finite).</summary>
        public readonly bool isFinite()
        {
            return IsFiniteD(_sign) && IsFiniteD(_layer) && IsFiniteD(_mag);
        }

        /// <summary>Exact equality of the normalized representation. NaN is never equal to anything (like double).</summary>
        public readonly bool eq(BigDouble value)
        {
            return _sign == value._sign && _layer == value._layer && _mag == value._mag;
        }

        /// <summary>Same as <see cref="eq(BigDouble)"/>.</summary>
        public readonly bool equals(BigDouble value)
        {
            return eq(value);
        }

        /// <summary>Negation of <see cref="eq(BigDouble)"/>.</summary>
        public readonly bool neq(BigDouble value)
        {
            return !eq(value);
        }

        /// <summary>Same as <see cref="neq(BigDouble)"/>.</summary>
        public readonly bool notEquals(BigDouble value)
        {
            return neq(value);
        }

        /// <summary>this &lt; value (false if either is NaN).</summary>
        public readonly bool lt(BigDouble value)
        {
            return CmpRaw(value) == -1;
        }

        /// <summary>this &lt;= value (false if either is NaN).</summary>
        public readonly bool lte(BigDouble value)
        {
            int c = CmpRaw(value);
            return c == -1 || c == 0;
        }

        /// <summary>this &gt; value (false if either is NaN).</summary>
        public readonly bool gt(BigDouble value)
        {
            return CmpRaw(value) == 1;
        }

        /// <summary>this &gt;= value (false if either is NaN).</summary>
        public readonly bool gte(BigDouble value)
        {
            int c = CmpRaw(value);
            return c == 1 || c == 0;
        }

        /// <summary>The larger of this and value (NaN if either is NaN).</summary>
        public readonly BigDouble max(BigDouble value)
        {
            if (isNan())
            {
                return this;
            }
            if (value.isNan())
            {
                return value;
            }
            return lt(value) ? value : this;
        }

        /// <summary>The smaller of this and value (NaN if either is NaN).</summary>
        public readonly BigDouble min(BigDouble value)
        {
            if (isNan())
            {
                return this;
            }
            if (value.isNan())
            {
                return value;
            }
            return gt(value) ? value : this;
        }

        /// <summary>Whichever of this and value has the larger absolute value.</summary>
        public readonly BigDouble maxabs(BigDouble value)
        {
            return cmpabs(value) < 0 ? value : this;
        }

        /// <summary>Whichever of this and value has the smaller absolute value.</summary>
        public readonly BigDouble minabs(BigDouble value)
        {
            return cmpabs(value) > 0 ? value : this;
        }

        /// <summary>Restricts this value to [min, max].</summary>
        public readonly BigDouble clamp(BigDouble min, BigDouble max)
        {
            return this.max(min).min(max);
        }

        /// <summary>Returns min if this is smaller than min.</summary>
        public readonly BigDouble clampMin(BigDouble min)
        {
            return max(min);
        }

        /// <summary>Returns max if this is greater than max.</summary>
        public readonly BigDouble clampMax(BigDouble max)
        {
            return min(max);
        }

        /// <summary>
        /// break_eternity.js eq_tolerance: compares the stored magnitudes relatively, NOT the values. For ordinary
        /// numbers (layer 0) that is a relative tolerance of the value. From layer 1 on it is a tolerance of the
        /// exponent: at 1e-7, 1e1000 equals 1.0001e1000, and 1e1000000000 equals 1e1000000001 (a factor of 10).
        /// The other *Tolerance comparisons use this as well. For a relative tolerance of the value on every layer use
        /// <see cref="ApproximatelyEquals"/>.
        /// </summary>
        public readonly bool eqTolerance(BigDouble value, double tolerance = 1e-7)
        {
            //Numbers that are too far away are never close.
            if (_sign != value._sign)
            {
                return false;
            }
            if (Math.Abs(_layer - value._layer) > 1)
            {
                return false;
            }
            // return abs(a-b) <= tolerance * max(abs(a), abs(b))
            double magA = _mag;
            double magB = value._mag;
            if (_layer > value._layer)
            {
                magB = f_maglog10(magB);
            }
            if (_layer < value._layer)
            {
                magA = f_maglog10(magA);
            }
            return Math.Abs(magA - magB) <= tolerance * Math.Max(Math.Abs(magA), Math.Abs(magB));
        }

        /// <summary>
        /// Like <see cref="cmp(BigDouble)"/>, but values that are equal by <see cref="eqTolerance(BigDouble, double)"/>
        /// (relative tolerance of the stored magnitude, i.e. of the exponent from layer 1 on) compare as 0.
        /// </summary>
        public readonly int cmpTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return eqTolerance(value, tolerance) ? 0 : cmp(value);
        }

        /// <summary>Same as <see cref="cmpTolerance(BigDouble, double)"/>.</summary>
        public readonly int compareTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return cmpTolerance(value, tolerance);
        }

        /// <summary>Same as <see cref="eqTolerance(BigDouble, double)"/>.</summary>
        public readonly bool equalsTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return eqTolerance(value, tolerance);
        }

        /// <summary>Negation of <see cref="eqTolerance(BigDouble, double)"/>.</summary>
        public readonly bool neqTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return !eqTolerance(value, tolerance);
        }

        /// <summary>Same as <see cref="neqTolerance(BigDouble, double)"/>.</summary>
        public readonly bool notEqualsTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return neqTolerance(value, tolerance);
        }

        /// <summary>this &lt; value and not approximately equal.</summary>
        public readonly bool ltTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return !eqTolerance(value, tolerance) && lt(value);
        }

        /// <summary>this &lt; value or approximately equal.</summary>
        public readonly bool lteTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return eqTolerance(value, tolerance) || lt(value);
        }

        /// <summary>this &gt; value and not approximately equal.</summary>
        public readonly bool gtTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return !eqTolerance(value, tolerance) && gt(value);
        }

        /// <summary>this &gt; value or approximately equal.</summary>
        public readonly bool gteTolerance(BigDouble value, double tolerance = 1e-7)
        {
            return eqTolerance(value, tolerance) || gt(value);
        }

        /// <summary>JS-compatible alias of <see cref="lte(BigDouble)"/>.</summary>
        public readonly bool lessThanOrEqualTo(BigDouble other)
        {
            return lte(other);
        }

        /// <summary>JS-compatible alias of <see cref="lt(BigDouble)"/>.</summary>
        public readonly bool lessThan(BigDouble other)
        {
            return lt(other);
        }

        /// <summary>JS-compatible alias of <see cref="gte(BigDouble)"/>.</summary>
        public readonly bool greaterThanOrEqualTo(BigDouble other)
        {
            return gte(other);
        }

        /// <summary>JS-compatible alias of <see cref="gt(BigDouble)"/>.</summary>
        public readonly bool greaterThan(BigDouble other)
        {
            return gt(other);
        }

        #endregion

        #region Logarithms

        /// <summary>
        /// "Positive log10": log10 for values &gt; 0, returns 0 for values &lt;= 0.
        /// (break_eternity.js returns NaN for exactly 0; 0 is returned here so the result is always usable.)
        /// </summary>
        public readonly BigDouble pLog10()
        {
            if (lte(dZero))
            {
                return dZero;
            }
            return log10();
        }

        /// <summary>log10(|this|).</summary>
        public readonly BigDouble absLog10()
        {
            if (_sign == 0)
            {
                return dNaN;
            }
            if (_layer > 0)
            {
                return FC(JsSign(_mag), _layer - 1, Math.Abs(_mag));
            }
            return FC(1, 0, Math.Log10(_mag));
        }

        /// <summary>Base-10 logarithm. NaN for values &lt;= 0.</summary>
        public readonly BigDouble log10()
        {
            if (!(_sign > 0))
            {
                return dNaN;
            }
            if (_layer > 0)
            {
                return FC(JsSign(_mag), _layer - 1, Math.Abs(_mag));
            }
            return FC(_sign, 0, Math.Log10(_mag));
        }

        /// <summary>Logarithm to an arbitrary base.</summary>
        public readonly BigDouble log(BigDouble @base)
        {
            if (!(_sign > 0))
            {
                return dNaN;
            }
            if (!(@base._sign > 0))
            {
                return dNaN;
            }
            if (@base._sign == 1 && @base._layer == 0 && @base._mag == 1)
            {
                return dNaN;
            }
            if (_layer == 0 && @base._layer == 0)
            {
                return FC(_sign, 0, Math.Log(_mag) / Math.Log(@base._mag));
            }
            return log10().div(@base.log10());
        }

        /// <summary>Base-2 logarithm.</summary>
        public readonly BigDouble log2()
        {
            if (!(_sign > 0))
            {
                return dNaN;
            }
            if (_layer == 0)
            {
                return FC(_sign, 0, Log2D(_mag));
            }
            if (_layer == 1)
            {
                return FC(JsSign(_mag), 0, Math.Abs(_mag) * 3.321928094887362); //log2(10)
            }
            if (_layer == 2)
            {
                return FC(JsSign(_mag), 1, Math.Abs(_mag) + 0.5213902276543247); //-log10(log10(2))
            }
            return FC(JsSign(_mag), _layer - 1, Math.Abs(_mag));
        }

        private static double Log2D(double x)
        {
            // Math.Log2 is not part of .NET Standard 2.0. Exact for powers of two.
            double r = Math.Log(x) / 0.69314718055994530942;
            double rounded = Math.Round(r);
            if (Math.Abs(r - rounded) < 1e-9 && Math.Pow(2, rounded) == x)
            {
                return rounded;
            }
            return r;
        }

        /// <summary>Natural logarithm.</summary>
        public readonly BigDouble ln()
        {
            if (!(_sign > 0))
            {
                return dNaN;
            }
            if (_layer == 0)
            {
                return FC(_sign, 0, Math.Log(_mag));
            }
            if (_layer == 1)
            {
                return FC(JsSign(_mag), 0, Math.Abs(_mag) * 2.302585092994046); //ln(10)
            }
            if (_layer == 2)
            {
                return FC(JsSign(_mag), 1, Math.Abs(_mag) + 0.36221568869946325); //log10(log10(e))
            }
            return FC(JsSign(_mag), _layer - 1, Math.Abs(_mag));
        }

        /// <summary>Same as <see cref="log(BigDouble)"/>.</summary>
        public readonly BigDouble logarithm(BigDouble @base)
        {
            return log(@base);
        }

        #endregion

        #region Powers and roots

        /// <summary>this ^ value. 0^0 = 1, 0^x = 0 (break_eternity.js behaviour).</summary>
        public readonly BigDouble pow(BigDouble value)
        {
            var a = this;
            var b = value;

            //special case: if a is 0, then return 0 (UNLESS b is 0, then return 1)
            if (a._sign == 0)
            {
                return b.eq(dZero) ? dOne : a;
            }
            //special case: if a is 1, then return 1
            if (a._sign == 1 && a._layer == 0 && a._mag == 1)
            {
                return a;
            }
            //special case: if b is 0, then return 1
            if (b._sign == 0)
            {
                return dOne;
            }
            //special case: if b is 1, then return a
            if (b._sign == 1 && b._layer == 0 && b._mag == 1)
            {
                return a;
            }

            // Two ordinary numbers: use Math.Pow while the result is an ordinary double. break_eternity.js always
            // computes 10^(b * log10(a)), which turns exact results into 7.999999999999999 (2^3) or
            // 1024.0000000000002 (2^10) - and Floor/Ceiling then answer 7 or 1025. Overflow, results below 1e-307
            // and NaN (negative base, fractional exponent) fall through to the general path.
            if (a._layer == 0 && b._layer == 0)
            {
                double direct = Math.Pow(a._sign * a._mag, b._sign * b._mag);
                if (IsFiniteD(direct) && Math.Abs(direct) >= 1e-307)
                {
                    return fromNumber(direct);
                }
            }

            var result = a.absLog10().mul(b).pow10();

            if (_sign == -1)
            {
                double bn = b.toNumber();
                if (Math.Abs(bn % 2) % 2 == 1)
                {
                    return result.neg();
                }
                if (Math.Abs(bn % 2) % 2 == 0)
                {
                    return result;
                }
                return dNaN;
            }

            return result;
        }

        /// <summary>10 ^ this.</summary>
        public readonly BigDouble pow10()
        {
            /*
            There are four cases we need to consider:
            1) positive sign, positive mag (e15, ee15): +1 layer (e.g. 10^15 becomes e15, 10^e15 becomes ee15)
            2) negative sign, positive mag (-e15, -ee15): +1 layer but sign and mag sign are flipped (e.g. 10^-15 becomes e-15, 10^-e15 becomes ee-15)
            3) positive sign, negative mag (e-15, ee-15): layer 0 case would have been handled in the Math.Pow check, so just return 1
            4) negative sign, negative mag (-e-15, -ee-15): layer 0 case would have been handled in the Math.Pow check, so just return 1
            */
            if (!IsFiniteD(_layer) || !IsFiniteD(_mag))
            {
                if (eq(dInf))
                {
                    return dInf;
                }
                if (eq(dNegInf))
                {
                    return dZero;
                }
                return dNaN;
            }

            var a = this;

            //handle layer 0 case - if no precision is lost just use Math.Pow, else promote one layer
            if (a._layer == 0)
            {
                if (a._sign == 0)
                {
                    return dOne;
                }
                // Inside the range of layer 0 the double is the result. Outside, the exponent itself is the stored mag.
                // break_eternity.js only kept results from 0.1 up and went through log10(x) and 10^log10(x) for the rest,
                // which cost 1-2 digits: 1.15^1e6 and sqrt(1e-30) were up to 70 ulps off.
                double exponent = a._sign * a._mag;
                if (exponent > -LAYER_DOWN && exponent < LAYER_DOWN)
                {
                    return fromNumber(Math.Pow(10, exponent));
                }
                return FC(1, 1, exponent);
            }

            //handle all 4 layer 1+ cases individually
            if (a._sign > 0 && a._mag >= 0)
            {
                return FC(a._sign, a._layer + 1, a._mag);
            }
            if (a._sign < 0 && a._mag >= 0)
            {
                return FC(-a._sign, a._layer + 1, -a._mag);
            }
            //both the negative mag cases are identical: one +/- rounding error
            return dOne;
        }

        /// <summary>value ^ this.</summary>
        public readonly BigDouble pow_base(BigDouble value)
        {
            return value.pow(this);
        }

        /// <summary>Same as <see cref="pow_base"/> (name of the previous C# port).</summary>
        public readonly BigDouble powBase(BigDouble value)
        {
            return value.pow(this);
        }

        /// <summary>The value-th root of this (this ^ (1/value)). Odd roots of negative numbers are negative.</summary>
        public readonly BigDouble root(BigDouble value)
        {
            if (lt(dZero) && value.mod(2, true).eq(dOne))
            {
                return neg().root(value).neg();
            }
            return pow(value.recip());
        }

        /// <summary>Factorial, extended to real numbers via the gamma function.</summary>
        public readonly BigDouble factorial()
        {
            if (_mag < 0)
            {
                return add(dOne).gamma();
            }
            if (_layer == 0)
            {
                return add(dOne).gamma();
            }
            if (_layer == 1)
            {
                return BigDouble.exp(BigDouble.mul(this, BigDouble.ln(this).sub(dOne)));
            }
            return BigDouble.exp(this);
        }

        /// <summary>The gamma function (gamma(x) = (x-1)! for positive integers).</summary>
        public readonly BigDouble gamma()
        {
            //from HyperCalc source code
            if (_mag < 0)
            {
                return recip();
            }
            if (_layer == 0)
            {
                // Positive integers: the product itself (the series below gives 24.000000000000075 for gamma(5)).
                if (_sign > 0 && _mag <= 171 && _mag == Math.Floor(_mag))
                {
                    double product = 1;
                    for (int i = 2; i < _mag; i++)
                    {
                        product *= i;
                    }
                    return fromNumber(product);
                }
                if (lt(FC_NN(1, 0, 24)))
                {
                    return fromNumber(f_gamma(_sign * _mag));
                }

                double t = _mag - 1;
                double l = 0.9189385332046727; //0.5*Math.log(2*Math.PI)
                l = l + (t + 0.5) * Math.Log(t);
                l = l - t;
                double n2 = t * t;
                double np = t;
                double lm = 12 * np;
                double adj = 1 / lm;
                double l2 = l + adj;
                if (l2 == l)
                {
                    return BigDouble.exp(l);
                }

                l = l2;
                np = np * n2;
                lm = 360 * np;
                adj = 1 / lm;
                l2 = l - adj;
                if (l2 == l)
                {
                    return BigDouble.exp(l);
                }

                l = l2;
                np = np * n2;
                lm = 1260 * np;
                double lt_ = 1 / lm;
                l = l + lt_;
                np = np * n2;
                lm = 1680 * np;
                lt_ = 1 / lm;
                l = l - lt_;
                return BigDouble.exp(l);
            }
            if (_layer == 1)
            {
                return BigDouble.exp(BigDouble.mul(this, BigDouble.ln(this).sub(dOne)));
            }
            return BigDouble.exp(this);
        }

        //from HyperCalc source code
        private static double f_gamma(double n)
        {
            if (!IsFiniteD(n))
            {
                return n;
            }
            if (n < -50)
            {
                if (n == Math.Truncate(n))
                {
                    return double.NegativeInfinity;
                }
                return 0;
            }

            double scal1 = 1;
            while (n < 10)
            {
                scal1 = scal1 * n;
                ++n;
            }

            n -= 1;
            double l = 0.9189385332046727; //0.5*Math.log(2*Math.PI)
            l = l + (n + 0.5) * Math.Log(n);
            l = l - n;
            double n2 = n * n;
            double np = n;
            l = l + 1 / (12 * np);
            np = np * n2;
            l = l - 1 / (360 * np);
            np = np * n2;
            l = l + 1 / (1260 * np);
            np = np * n2;
            l = l - 1 / (1680 * np);
            np = np * n2;
            l = l + 1 / (1188 * np);
            np = np * n2;
            l = l - 691 / (360360 * np);
            np = np * n2;
            l = l + 7 / (1092 * np);
            np = np * n2;
            l = l - 3617 / (122400 * np);

            return Math.Exp(l) / scal1;
        }

        /// <summary>ln(gamma(this)).</summary>
        public readonly BigDouble lngamma()
        {
            return gamma().ln();
        }

        /// <summary>e ^ this.</summary>
        public readonly BigDouble exp()
        {
            if (eq(dNegInf))
            {
                return dZero;
            }
            if (_mag < 0)
            {
                return dOne;
            }
            if (_layer == 0 && _mag <= 709.7)
            {
                return fromNumber(Math.Exp(_sign * _mag));
            }
            if (_layer == 0)
            {
                return FC(1, 1, _sign * Math.Log10(Math.E) * _mag);
            }
            if (_layer == 1)
            {
                return FC(1, 2, _sign * (Math.Log10(0.4342944819032518) + _mag));
            }
            return FC(1, _layer + 1, _sign * _mag);
        }

        /// <summary>this * this.</summary>
        public readonly BigDouble sqr()
        {
            return pow(dTwo);
        }

        /// <summary>Square root. NaN for negative values.</summary>
        public readonly BigDouble sqrt()
        {
            if (_layer == 0)
            {
                return fromNumber(Math.Sqrt(_sign * _mag));
            }
            if (!IsFiniteD(_layer))
            {
                return _sign < 0 || isNan() ? dNaN : this;
            }
            if (_sign < 0)
            {
                return dNaN;
            }
            // break_eternity.js used a layer-2 shortcut here that returned NaN for numbers below 1e-16 and the
            // wrong value for tiny layer 2 numbers. pow(0.5) is exact for layer 1 and identical above.
            return pow(0.5);
        }

        /// <summary>this ^ 3.</summary>
        public readonly BigDouble cube()
        {
            return pow(3);
        }

        /// <summary>Cube root (negative for negative values).</summary>
        public readonly BigDouble cbrt()
        {
            if (lt(dZero))
            {
                return neg().pow(1.0 / 3.0).neg();
            }
            return pow(1.0 / 3.0);
        }

        #endregion

        #region Trigonometry

        /// <summary>
        /// Sine (only meaningful for layer 0 values; huge finite values give 0 like break_eternity.js).
        /// NaN and ±Infinity give NaN (break_eternity.js returned 0, which hid broken values).
        /// </summary>
        public readonly BigDouble sin()
        {
            if (!isFinite())
            {
                return dNaN;
            }
            if (_mag < 0)
            {
                return this;
            }
            if (_layer == 0)
            {
                return fromNumber(Math.Sin(_sign * _mag));
            }
            return dZero;
        }

        /// <summary>
        /// Cosine (only meaningful for layer 0 values; huge finite values give 0 like break_eternity.js).
        /// NaN and ±Infinity give NaN.
        /// </summary>
        public readonly BigDouble cos()
        {
            if (!isFinite())
            {
                return dNaN;
            }
            if (_mag < 0)
            {
                return dOne;
            }
            if (_layer == 0)
            {
                return fromNumber(Math.Cos(_sign * _mag));
            }
            return dZero;
        }

        /// <summary>
        /// Tangent (only meaningful for layer 0 values; huge finite values give 0 like break_eternity.js).
        /// NaN and ±Infinity give NaN.
        /// </summary>
        public readonly BigDouble tan()
        {
            if (!isFinite())
            {
                return dNaN;
            }
            if (_mag < 0)
            {
                return this;
            }
            if (_layer == 0)
            {
                return fromNumber(Math.Tan(_sign * _mag));
            }
            return dZero;
        }

        /// <summary>Arcsine.</summary>
        public readonly BigDouble asin()
        {
            if (_mag < 0)
            {
                return this;
            }
            if (_layer == 0)
            {
                return fromNumber(Math.Asin(_sign * _mag));
            }
            return dNaN;
        }

        /// <summary>Arccosine.</summary>
        public readonly BigDouble acos()
        {
            if (_mag < 0)
            {
                return fromNumber(Math.Acos(toNumber()));
            }
            if (_layer == 0)
            {
                return fromNumber(Math.Acos(_sign * _mag));
            }
            return dNaN;
        }

        /// <summary>Arctangent.</summary>
        public readonly BigDouble atan()
        {
            if (_mag < 0)
            {
                return this;
            }
            if (_layer == 0)
            {
                return fromNumber(Math.Atan(_sign * _mag));
            }
            return fromNumber(Math.Atan(_sign * double.PositiveInfinity));
        }

        // Hyperbolic functions: break_eternity.js evaluates exp(x) - exp(-x), ln(x + sqrt(x^2 + 1)) etc. for every
        // input, which cancels to 0 for small |x| (sinh(1e-20) = 0, relative error 8e-8 at 1e-10). Values below
        // 1.1e-16 (stored with negative mag) return x or 1 directly, ordinary numbers use System.Math or log1p-based
        // formulas, and only large values use the BigDouble formulas.

        /// <summary>Hyperbolic sine.</summary>
        public readonly BigDouble sinh()
        {
            if (isNan())
            {
                return dNaN;
            }
            if (_mag < 0)
            {
                return this; // |x| < 1.1e-16: sinh(x) == x in double precision
            }
            if (_layer == 0 && _mag <= 709)
            {
                return fromNumber(Math.Sinh(_sign * _mag));
            }
            return exp().sub(negate().exp()).div(dTwo);
        }

        /// <summary>Hyperbolic cosine.</summary>
        public readonly BigDouble cosh()
        {
            if (isNan())
            {
                return dNaN;
            }
            if (_mag < 0)
            {
                return dOne;
            }
            if (_layer == 0 && _mag <= 709)
            {
                return fromNumber(Math.Cosh(_sign * _mag));
            }
            return exp().add(negate().exp()).div(dTwo);
        }

        /// <summary>Hyperbolic tangent (±1 for ±Infinity and other huge values).</summary>
        public readonly BigDouble tanh()
        {
            if (isNan())
            {
                return dNaN;
            }
            if (_mag < 0)
            {
                return this;
            }
            if (_layer == 0)
            {
                return fromNumber(Math.Tanh(_sign * _mag));
            }
            return fromNumber(_sign); // |x| >= 9e15 or infinite
        }

        /// <summary>Inverse hyperbolic sine.</summary>
        public readonly BigDouble asinh()
        {
            if (isNan())
            {
                return dNaN;
            }
            if (_mag < 0)
            {
                return this;
            }
            // asinh is odd. break_eternity.js evaluates ln(x + sqrt(x^2 + 1)) for negative x as well, where
            // x + sqrt(x^2 + 1) cancels to 0 for large |x| and the result becomes NaN or garbage.
            if (_sign < 0)
            {
                return neg().asinh().neg();
            }
            if (_layer == 0 && _mag < 1e8)
            {
                // asinh(x) = log1p(x + x^2 / (1 + sqrt(1 + x^2)))
                double x = _mag;
                return fromNumber(Log1p(x + x * x / (1 + Math.Sqrt(1 + x * x))));
            }
            return BigDouble.ln(add(sqr().add(dOne).sqrt()));
        }

        /// <summary>Inverse hyperbolic cosine (NaN below 1).</summary>
        public readonly BigDouble acosh()
        {
            if (isNan() || lt(dOne))
            {
                return dNaN;
            }
            if (_layer == 0 && _mag < 1e8)
            {
                // acosh(x) = log1p((x - 1) + sqrt((x - 1) * (x + 1))), accurate close to 1
                double x = _mag;
                return fromNumber(Log1p((x - 1) + Math.Sqrt((x - 1) * (x + 1))));
            }
            return BigDouble.ln(add(sqr().sub(dOne).sqrt()));
        }

        /// <summary>Inverse hyperbolic tangent (NaN outside (-1, 1)).</summary>
        public readonly BigDouble atanh()
        {
            if (isNan() || abs().gte(dOne))
            {
                return dNaN;
            }
            if (_mag < 0)
            {
                return this;
            }
            // atanh(x) = sign(x) * 0.5 * log1p(2|x| / (1 - |x|)); |x| < 1 means layer 0 here. Working with |x| keeps
            // the function odd and 1 - |x| exact near 1 (for x near -1, 1 - x rounds to 2 and the quotient loses digits).
            double a = _mag;
            return fromNumber(_sign * 0.5 * Log1p(2 * a / (1 - a)));
        }

        /// <summary>e^x - 1, accurate for small x (Math.Expm1 does not exist in .NET Standard 2.0).</summary>
        private static double Expm1(double x)
        {
            double u = Math.Exp(x);
            if (u == 1)
            {
                return x;
            }
            double um1 = u - 1;
            if (um1 == -1 || double.IsInfinity(u))
            {
                return um1;
            }
            return um1 * x / Math.Log(u);
        }

        /// <summary>ln(1 + y), accurate for small y (Math.Log1p does not exist in .NET Standard 2.0).</summary>
        private static double Log1p(double y)
        {
            double u = 1 + y;
            if (u == 1)
            {
                return y;
            }
            return Math.Log(u) * y / (u - 1);
        }

        #endregion

        #region Joke functions from break_eternity.js

        /// <summary>Joke function from Realm Grinder.</summary>
        public readonly BigDouble ascensionPenalty(BigDouble ascensions)
        {
            if (ascensions.eq(dZero))
            {
                return this;
            }
            return root(BigDouble.pow(10, ascensions));
        }

        /// <summary>Joke function from Cookie Clicker. It's 'egg'.</summary>
        public readonly BigDouble egg()
        {
            return add(9);
        }

        #endregion

        #region Purchase / series helpers (incremental game math)

        // Purchase contract (stricter than break_eternity.js, whose closed formulas were floored directly and could be
        // one item off at exact price boundaries): afford*(S, ...) returns the largest whole n >= 0 with
        // sum*(n, ...) <= S, checked against the library's own sum* cost function. The closed formula only provides
        // the estimate. Supported inputs: priceStart > 0, priceRatio >= 1, priceAdd >= 0.

        /// <summary>
        /// If you're willing to spend 'resourcesAvailable' and want to buy something with exponentially increasing
        /// cost each purchase (start at priceStart, multiply by priceRatio, already own currentOwned),
        /// how much of it can you buy? Returns the largest n with sumGeometricSeries(n, ...) &lt;= resourcesAvailable.
        /// priceRatio = 1 (constant price) is supported. Adapted from Trimps source code.
        /// </summary>
        public static BigDouble affordGeometricSeries(BigDouble resourcesAvailable, BigDouble priceStart, BigDouble priceRatio, BigDouble currentOwned)
        {
            BigDouble estimate;
            if (priceRatio.eq(dOne))
            {
                estimate = resourcesAvailable.div(priceStart).floor();
            }
            else
            {
                var actualStart = priceStart.mul(priceRatio.pow(currentOwned));
                var exact = resourcesAvailable
                    .div(actualStart)
                    .mul(priceRatio.sub(dOne))
                    .add(dOne)
                    .log10()
                    .div(priceRatio.log10());
                // A relative error e of the budget or price moves the estimate by up to e / ln(ratio), whatever n is.
                if (resourcesAvailable._sign > 0 && priceRatio._layer == 0 && priceRatio._sign > 0 && priceRatio._mag >= 1 + 1e-6
                    && priceStart._sign > 0 && currentOwned._sign >= 0
                    && IsClearlyBetweenIntegers(exact, InputError(LogSize(resourcesAvailable) + LogSize(actualStart)) / Math.Log(priceRatio._mag) + 2.2e-13 * (exact._mag + 1)))
                {
                    return exact.floor();
                }
                estimate = exact.floor();
            }
            return RefineAffordable(estimate, resourcesAvailable, true, priceStart, priceRatio, currentOwned);
        }

        // |log10| of a value in layer 0 or 1; infinite above, which rules the shortcut below out.
        private static double LogSize(BigDouble value)
        {
            return value._layer == 0 ? Math.Abs(Math.Log10(value._mag)) : value._layer == 1 ? Math.Abs(value._mag) : double.PositiveInfinity;
        }

        // Relative rounding error of values of this log size (about 5e-16 * (1 + log size)), times a safety factor of 1000.
        private static double InputError(double logSize)
        {
            return 5e-13 * (1 + logSize);
        }

        // The purchase formulas compare the budget with sum(n) for two neighbouring n. That is only needed when the
        // closed formula lands so close to a whole number that rounding could decide the result. margin bounds the error of
        // the estimate; clearly between two whole numbers, floor(estimate) is the result already.
        private static bool IsClearlyBetweenIntegers(BigDouble estimate, double margin)
        {
            if (estimate._layer != 0 || estimate._sign <= 0 || estimate._mag >= 1e9)
            {
                return false;
            }
            double fraction = estimate._mag - Math.Floor(estimate._mag);
            return margin < 0.25 && fraction > margin && fraction < 1 - margin;
        }

        /// <summary>
        /// How much resource would it cost to buy (numItems) items if you already have currentOwned,
        /// the initial price is priceStart and it multiplies by priceRatio each purchase?
        /// priceRatio = 1 (constant price) is supported.
        /// </summary>
        public static BigDouble sumGeometricSeries(BigDouble numItems, BigDouble priceStart, BigDouble priceRatio, BigDouble currentOwned)
        {
            if (priceRatio.eq(dOne))
            {
                return priceStart.mul(numItems);
            }
            if (numItems._layer == 0 && priceRatio._layer == 0)
            {
                double n = numItems._sign * numItems._mag;
                double ratio = priceRatio._sign * priceRatio._mag;
                bool fewItems = n >= 0 && n <= DIRECT_SUM_LIMIT && n == Math.Floor(n);

                // Few items at ordinary prices: add up the item prices start * ratio^(owned + j) one by one, so the cost
                // equals the sum of the individual prices (two items at 10 and 10 * 1.00000001 cost 10 + 10 * 1.00000001,
                // and one item costs exactly its price). break_eternity.js returns 13.000000000000002 for
                // SumGeometricSeries(1, 13, 1.24, 0).
                if (fewItems && priceStart._layer == 0 && currentOwned._layer == 0)
                {
                    double start = priceStart._sign * priceStart._mag;
                    double owned = currentOwned._sign * currentOwned._mag;
                    double total = 0;
                    for (int j = 0; j < n; j++)
                    {
                        total += start * Math.Pow(ratio, owned + j);
                    }
                    if (IsFiniteD(total))
                    {
                        return fromNumber(total);
                    }
                }

                // Any other price: the series factor 1 + r + ... + r^(n-1) does not depend on the price, so it is computed
                // in double precision and the price (1e1000, 1e-100, a huge owned count ...) is applied afterwards.
                double factor = GeometricFactor(n, ratio, fewItems);
                if (IsFiniteD(factor))
                {
                    return priceStart.mul(priceRatio.pow(currentOwned).mul(factor));
                }
            }
            var general = dOne.sub(priceRatio.pow(numItems)).div(dOne.sub(priceRatio));
            return priceStart.mul(priceRatio.pow(currentOwned)).mul(general);
        }

        /// <summary>1 + r + r^2 + ... + r^(n-1) in double precision, without cancellation for r near 1.</summary>
        private static double GeometricFactor(double n, double ratio, bool fewItems)
        {
            if (fewItems)
            {
                double sum = 0;
                for (int j = 0; j < n; j++)
                {
                    sum += Math.Pow(ratio, j);
                }
                return sum;
            }
            // Near ratio 1, r^n - 1 cancels (1.00000001^2 - 1 keeps only 8 digits). While r^n < 2 it is computed as
            // expm1(n * log1p(r - 1)) instead; r - 1 is exact for 0.5 <= r <= 2.
            double d = ratio - 1;
            double x = n * Log1p(d);
            if (Math.Abs(x) < LN2)
            {
                return Expm1(x) / d;
            }
            return (Math.Pow(ratio, n) - 1) / d;
        }

        // Item counts up to which sumGeometricSeries adds the individual prices instead of using the closed formula.
        private const int DIRECT_SUM_LIMIT = 32;
        private const double LN2 = 0.6931471805599453;

        /// <summary>
        /// If you're willing to spend 'resourcesAvailable' and want to buy something with additively increasing cost
        /// each purchase (start at priceStart, add by priceAdd, already own currentOwned), how much of it can you buy?
        /// Returns the largest n with sumArithmeticSeries(n, ...) &lt;= resourcesAvailable. priceAdd = 0 is supported.
        /// </summary>
        public static BigDouble affordArithmeticSeries(BigDouble resourcesAvailable, BigDouble priceStart, BigDouble priceAdd, BigDouble currentOwned)
        {
            var actualStart = priceStart.add(currentOwned.mul(priceAdd));
            BigDouble estimate;
            if (priceAdd.eq(dZero))
            {
                estimate = resourcesAvailable.div(actualStart).floor();
            }
            else
            {
                // n = (-b + sqrt(b^2 + 2dS)) / d with b = a - d/2, where a is actualStart, d is priceAdd and S is
                // resourcesAvailable. For b > 0 the equivalent form 2S / (b + sqrt(b^2 + 2dS)) is used: it avoids the
                // cancellation of -b + sqrt(...) when the budget is small compared to the price (e.g. S = a = 1e100).
                var b = actualStart.sub(priceAdd.div(dTwo));
                var root = b.mul(b).add(priceAdd.mul(resourcesAvailable).mul(dTwo)).sqrt();
                estimate = b.gt(dZero)
                    ? resourcesAvailable.mul(dTwo).div(b.add(root))
                    : b.neg().add(root).div(priceAdd);
                // A relative error e moves the estimate by up to e * n, and by e * a / d because b = a - d/2 can cancel.
                if (resourcesAvailable._sign > 0 && priceAdd._sign > 0 && priceStart._sign > 0 && currentOwned._sign >= 0
                    && actualStart._layer == 0 && priceAdd._layer == 0
                    && IsClearlyBetweenIntegers(estimate, InputError(LogSize(resourcesAvailable) + LogSize(actualStart) + LogSize(priceAdd)) * (actualStart._mag / priceAdd._mag + estimate._mag + 1)))
                {
                    return estimate.floor();
                }
                estimate = estimate.floor();
            }
            return RefineAffordable(estimate, resourcesAvailable, false, priceStart, priceAdd, currentOwned);
        }

        private const double MAX_EXACT_INTEGER = 9007199254740992; // 2^53: above this, n + 1 == n in double precision

        /// <summary>
        /// Turns the estimate of a closed purchase formula into the largest whole n >= 0 whose cost fits the budget,
        /// using the same cost function the caller would use to pay. Estimates of 2^53 and above are returned as they
        /// are, because a single item no longer changes such a value.
        /// </summary>
        private static BigDouble RefineAffordable(BigDouble estimate, BigDouble budget, bool geometric, BigDouble priceStart, BigDouble step, BigDouble currentOwned)
        {
            if (budget.isNan())
            {
                return dNaN;
            }
            if (budget._sign < 0)
            {
                // Not even zero items fit; the closed formulas would yield NaN or negative counts here.
                return dZero;
            }
            if (estimate.isNan())
            {
                return dNaN;
            }
            double n;
            if (estimate._sign <= 0)
            {
                n = 0;
            }
            else if (estimate._layer == 0 && estimate._mag < MAX_EXACT_INTEGER)
            {
                n = Math.Floor(estimate._mag);
            }
            else
            {
                return estimate;
            }

            bool Fits(double count)
            {
                var cost = geometric
                    ? sumGeometricSeries(count, priceStart, step, currentOwned)
                    : sumArithmeticSeries(count, priceStart, step, currentOwned);
                return cost.lte(budget);
            }

            double lo, hi;
            double stride = 1;
            int guard = 0;
            if (Fits(n))
            {
                // Search upwards: lo fits, hi does not.
                lo = n;
                hi = n + 1;
                while (Fits(hi))
                {
                    lo = hi;
                    stride *= 2;
                    hi = n + stride;
                    if (hi >= MAX_EXACT_INTEGER || ++guard > 64)
                    {
                        return fromNumber(lo);
                    }
                }
            }
            else
            {
                // Search downwards. Buying nothing always fits a non-negative budget.
                hi = n;
                lo = n - 1;
                while (lo > 0 && !Fits(lo))
                {
                    hi = lo;
                    stride *= 2;
                    lo = Math.Max(0, n - stride);
                    if (++guard > 64)
                    {
                        break;
                    }
                }
                if (lo <= 0)
                {
                    lo = 0;
                    if (!Fits(0))
                    {
                        return dZero;
                    }
                }
            }
            while (hi - lo > 1)
            {
                double mid = Math.Floor((lo + hi) / 2);
                if (Fits(mid))
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            return fromNumber(lo);
        }

        /// <summary>
        /// How much resource would it cost to buy (numItems) items if you already have currentOwned,
        /// the initial price is priceStart and it adds priceAdd each purchase?
        /// </summary>
        public static BigDouble sumArithmeticSeries(BigDouble numItems, BigDouble priceStart, BigDouble priceAdd, BigDouble currentOwned)
        {
            var actualStart = priceStart.add(currentOwned.mul(priceAdd)); // (n/2)*(2*a+(n-1)*d)
            return numItems.div(dTwo).mul(actualStart.mul(dTwo).plus(numItems.sub(dOne).mul(priceAdd)));
        }

        /// <summary>
        /// When comparing two purchases that cost (resource) and increase your resource/sec by (deltaRpS),
        /// the lowest efficiency score is the better one to purchase. From Frozen Cookies.
        /// </summary>
        public static BigDouble efficiencyOfPurchase(BigDouble cost, BigDouble currentRpS, BigDouble deltaRpS)
        {
            return cost.div(currentRpS).add(cost.div(deltaRpS));
        }

        /// <summary>
        /// Random BigDouble for tests. Does not follow any sane distribution - testing purposes only.
        /// </summary>
        public static BigDouble randomDecimalForTesting(double maxLayers, Random random = null)
        {
            var rand = random ?? new Random();
            //5% of the time, return 0
            if (rand.NextDouble() * 20 < 1)
            {
                return dZero;
            }

            double randomsign = rand.NextDouble() > 0.5 ? 1 : -1;

            //5% of the time, return 1 or -1
            if (rand.NextDouble() * 20 < 1)
            {
                return FC_NN(randomsign, 0, 1);
            }

            //pick a random layer
            double layer = Math.Floor(rand.NextDouble() * (maxLayers + 1));

            double randomexp = layer == 0 ? rand.NextDouble() * 616 - 308 : rand.NextDouble() * 16;
            //10% of the time, make it a simple power of 10
            if (rand.NextDouble() > 0.9)
            {
                randomexp = Math.Truncate(randomexp);
            }
            double randommag = Math.Pow(10, randomexp);
            //10% of the time, trunc mag
            if (rand.NextDouble() > 0.9)
            {
                randommag = Math.Truncate(randommag);
            }
            return FC(randomsign, layer, randommag);
        }

        #endregion

        // ============================================================================================================
        // 2. C#-style API of BigDouble: operators, conversions, equality/ordering and static methods modelled after System.Math.
        //
        //   BigDouble joule = 100;            // implicit from int/long/float/double
        //   joule *= 1.15;                    // all arithmetic and comparison operators
        //   if (joule >= cost) joule -= cost;
        //   BigDouble big = BigDouble.Parse("1e1000000000");
        //   double d = (double)joule;         // explicit: may overflow to Infinity
        //   BigDouble.Pow(a, b), BigDouble.Log10(x), BigDouble.Max(a, b), BigDouble.Tetrate(10, 3) ...
        //
        // ============================================================================================================

        #region Operators

        /// <summary>Addition.</summary>
        public static BigDouble operator +(BigDouble left, BigDouble right)
        {
            return left.add(right);
        }

        /// <summary>Subtraction.</summary>
        public static BigDouble operator -(BigDouble left, BigDouble right)
        {
            return left.sub(right);
        }

        /// <summary>Multiplication.</summary>
        public static BigDouble operator *(BigDouble left, BigDouble right)
        {
            return left.mul(right);
        }

        /// <summary>Division. Division by zero returns NaN.</summary>
        public static BigDouble operator /(BigDouble left, BigDouble right)
        {
            return left.div(right);
        }

        /// <summary>Remainder with truncated division (like % on doubles). x % 0 returns 0.</summary>
        public static BigDouble operator %(BigDouble left, BigDouble right)
        {
            return left.mod(right);
        }

        /// <summary>Negation.</summary>
        public static BigDouble operator -(BigDouble value)
        {
            return value.neg();
        }

        /// <summary>Unary plus (returns the value unchanged).</summary>
        public static BigDouble operator +(BigDouble value)
        {
            return value;
        }

        /// <summary>Adds 1. Above ~9e15 adding 1 no longer changes the value (limited precision, like double).</summary>
        public static BigDouble operator ++(BigDouble value)
        {
            return value.add(dOne);
        }

        /// <summary>Subtracts 1.</summary>
        public static BigDouble operator --(BigDouble value)
        {
            return value.sub(dOne);
        }

        /// <summary>Exact equality (NaN is not equal to anything, like double).</summary>
        public static bool operator ==(BigDouble left, BigDouble right)
        {
            return left.eq(right);
        }

        /// <summary>Exact inequality.</summary>
        public static bool operator !=(BigDouble left, BigDouble right)
        {
            return !left.eq(right);
        }

        /// <summary>Less than (false if either side is NaN).</summary>
        public static bool operator <(BigDouble left, BigDouble right)
        {
            return left.lt(right);
        }

        /// <summary>Less than or equal (false if either side is NaN).</summary>
        public static bool operator <=(BigDouble left, BigDouble right)
        {
            return left.lte(right);
        }

        /// <summary>Greater than (false if either side is NaN).</summary>
        public static bool operator >(BigDouble left, BigDouble right)
        {
            return left.gt(right);
        }

        /// <summary>Greater than or equal (false if either side is NaN).</summary>
        public static bool operator >=(BigDouble left, BigDouble right)
        {
            return left.gte(right);
        }

        #endregion

        #region Conversions

        /// <summary>
        /// Implicit conversion from double. int, long, float etc. convert implicitly through double (longs above 2^53
        /// are rounded, exactly like a double). Exact for |value| in [1.1e-16, 9e15); outside that range the value is
        /// stored as its base-10 logarithm (~15 significant digits).
        /// </summary>
        public static implicit operator BigDouble(double value)
        {
            return fromNumber(value);
        }

        /// <summary>Explicit conversion to double: values beyond ±1.79e308 become ±Infinity, tiny values 0.</summary>
        public static explicit operator double(BigDouble value)
        {
            return value.toNumber();
        }

        /// <summary>Explicit conversion to float: values beyond ±3.4e38 become ±Infinity.</summary>
        public static explicit operator float(BigDouble value)
        {
            return (float)value.toNumber();
        }

        /// <summary>
        /// Explicit conversion to long: truncates towards zero and saturates at long.MinValue/long.MaxValue. NaN becomes 0.
        /// </summary>
        public static explicit operator long(BigDouble value)
        {
            double d = value.toNumber();
            if (double.IsNaN(d))
            {
                return 0;
            }
            if (d >= 9223372036854775807.0)
            {
                return long.MaxValue;
            }
            if (d <= -9223372036854775808.0)
            {
                return long.MinValue;
            }
            return (long)d;
        }

        /// <summary>
        /// Explicit conversion to int: truncates towards zero and saturates at int.MinValue/int.MaxValue. NaN becomes 0.
        /// </summary>
        public static explicit operator int(BigDouble value)
        {
            double d = value.toNumber();
            if (double.IsNaN(d))
            {
                return 0;
            }
            if (d >= int.MaxValue)
            {
                return int.MaxValue;
            }
            if (d <= int.MinValue)
            {
                return int.MinValue;
            }
            return (int)d;
        }

        /// <summary>Explicit conversion from string (same as <see cref="Parse(string)"/>; throws on invalid input).</summary>
        public static explicit operator BigDouble(string value)
        {
            return Parse(value);
        }

        /// <summary>Converts to double. Values beyond ±1.79e308 become ±Infinity, tiny values become 0.</summary>
        public readonly double ToDouble()
        {
            return toNumber();
        }

        /// <summary>Same as <see cref="ToDouble"/> (name of the previous C# port).</summary>
        public readonly double toDouble()
        {
            return toNumber();
        }

        /// <summary>Converts to float (±Infinity beyond the float range).</summary>
        public readonly float ToFloat()
        {
            return (float)toNumber();
        }

        /// <summary>
        /// Converts to double if the value fits into the double range: false (and result 0) for NaN, ±Infinity, values
        /// beyond ±1.79e308 and non-zero values that would become 0 (below 5e-324). True does not mean bit-exact:
        /// like every double conversion the result is the nearest double (values from 9e15 on are stored as log10).
        /// </summary>
        public readonly bool TryToDouble(out double result)
        {
            double converted = toNumber();
            if (IsFiniteD(converted) && (converted != 0 || _sign == 0))
            {
                result = converted;
                return true;
            }
            result = 0;
            return false;
        }

        /// <summary>
        /// Converts to float if the value fits into the float range (for Unity APIs such as Slider.value or
        /// Vector3): false (and result 0) for NaN, ±Infinity, values beyond ±3.4e38 and non-zero values that would
        /// become 0 (below 1.4e-45). True does not mean exact: the result is the nearest float.
        /// </summary>
        public readonly bool TryToFloat(out float result)
        {
            float converted = (float)toNumber();
            if (!float.IsNaN(converted) && !float.IsInfinity(converted) && (converted != 0 || _sign == 0))
            {
                result = converted;
                return true;
            }
            result = 0;
            return false;
        }

        #endregion

        #region IComparable / IEquatable

        /// <summary>Total order for sorting: NaN &lt; -Infinity &lt; ... &lt; Infinity (like double.CompareTo).</summary>
        public readonly int CompareTo(BigDouble other)
        {
            return cmp(other);
        }

        /// <summary>Compares with another BigDouble (null counts as smaller).</summary>
        public readonly int CompareTo(object obj)
        {
            if (obj == null)
            {
                return 1;
            }
            if (obj is BigDouble other)
            {
                return cmp(other);
            }
            throw new ArgumentException("Object must be of type BigDouble.", nameof(obj));
        }

        /// <summary>Exact equality; unlike ==, NaN.Equals(NaN) is true (like double.Equals), so NaN works as a dictionary key.</summary>
        public readonly bool Equals(BigDouble other)
        {
            return eq(other) || (isNan() && other.isNan());
        }

        /// <inheritdoc/>
        public override readonly bool Equals(object obj)
        {
            return obj is BigDouble other && Equals(other);
        }

        /// <inheritdoc/>
        public override readonly int GetHashCode()
        {
            if (_sign == 0)
            {
                return 0;
            }
            if (isNan())
            {
                return int.MinValue;
            }
            unchecked
            {
                int hash = _sign.GetHashCode();
                hash = hash * 397 ^ _layer.GetHashCode();
                hash = hash * 397 ^ _mag.GetHashCode();
                return hash;
            }
        }

        #endregion

        #region Static API (System.Math style)

        /// <summary>Creates a BigDouble from its components (normalized).</summary>
        public static BigDouble FromComponents(double sign, double layer, double mag)
        {
            return FC(sign, layer, mag);
        }

        /// <summary>mantissa * 10^exponent, for exponents far beyond the double range (e.g. FromMantissaExponent(1, 1e9)).</summary>
        public static BigDouble FromMantissaExponent(double mantissa, double exponent)
        {
            return fromMantissaExponent(mantissa, exponent);
        }

        /// <summary>True if value is NaN.</summary>
        public static bool IsNaN(BigDouble value)
        {
            return value.isNan();
        }

        /// <summary>True if value is neither NaN nor ±Infinity.</summary>
        public static bool IsFinite(BigDouble value)
        {
            return value.isFinite();
        }

        /// <summary>True if value is +Infinity or -Infinity.</summary>
        public static bool IsInfinity(BigDouble value)
        {
            return !value.isNan() && !value.isFinite();
        }

        /// <summary>True if value is +Infinity.</summary>
        public static bool IsPositiveInfinity(BigDouble value)
        {
            return IsInfinity(value) && value._sign > 0;
        }

        /// <summary>True if value is -Infinity.</summary>
        public static bool IsNegativeInfinity(BigDouble value)
        {
            return IsInfinity(value) && value._sign < 0;
        }

        /// <summary>True if value is 0.</summary>
        public static bool IsZero(BigDouble value)
        {
            return value._sign == 0;
        }

        /// <summary>Absolute value.</summary>
        public static BigDouble Abs(BigDouble value)
        {
            return value.abs();
        }

        /// <summary>Negation (-value).</summary>
        public static BigDouble Negate(BigDouble value)
        {
            return value.neg();
        }

        /// <summary>-1, 0 or 1 (like Math.Sign). Throws <see cref="ArithmeticException"/> for NaN, like Math.Sign.</summary>
        public static int Sign(BigDouble value)
        {
            if (value.isNan())
            {
                throw new ArithmeticException("Function does not accept NaN values.");
            }
            return value._sign > 0 ? 1 : value._sign < 0 ? -1 : 0;
        }

        /// <summary>a + b</summary>
        public static BigDouble Add(BigDouble a, BigDouble b)
        {
            return a.add(b);
        }

        /// <summary>a - b</summary>
        public static BigDouble Subtract(BigDouble a, BigDouble b)
        {
            return a.sub(b);
        }

        /// <summary>a * b</summary>
        public static BigDouble Multiply(BigDouble a, BigDouble b)
        {
            return a.mul(b);
        }

        /// <summary>a / b (NaN if b is 0).</summary>
        public static BigDouble Divide(BigDouble a, BigDouble b)
        {
            return a.div(b);
        }

        /// <summary>
        /// Remainder of a / b. Truncated (sign of a, like %) by default; floored = true gives the sign of b.
        /// </summary>
        public static BigDouble Mod(BigDouble a, BigDouble b, bool floored = false)
        {
            return a.mod(b, floored);
        }

        /// <summary>1 / value (NaN for 0).</summary>
        public static BigDouble Reciprocal(BigDouble value)
        {
            return value.recip();
        }

        /// <summary>Total-order comparison (-1, 0, 1).</summary>
        public static int Compare(BigDouble a, BigDouble b)
        {
            return a.cmp(b);
        }

        /// <summary>The larger value (NaN if either is NaN, like Math.Max).</summary>
        public static BigDouble Max(BigDouble a, BigDouble b)
        {
            return a.max(b);
        }

        /// <summary>The smaller value (NaN if either is NaN, like Math.Min).</summary>
        public static BigDouble Min(BigDouble a, BigDouble b)
        {
            return a.min(b);
        }

        /// <summary>The value with the larger absolute value.</summary>
        public static BigDouble MaxAbs(BigDouble a, BigDouble b)
        {
            return a.maxabs(b);
        }

        /// <summary>The value with the smaller absolute value.</summary>
        public static BigDouble MinAbs(BigDouble a, BigDouble b)
        {
            return a.minabs(b);
        }

        /// <summary>Restricts value to [min, max].</summary>
        public static BigDouble Clamp(BigDouble value, BigDouble min, BigDouble max)
        {
            return value.clamp(min, max);
        }

        /// <summary>
        /// Linear interpolation a + (b - a) * t with t clamped to [0, 1], like Mathf.Lerp: t = 0 gives exactly a,
        /// t = 1 exactly b. End points of opposite sign are interpolated accurately through zero
        /// (Lerp(-8e15, 8e15, 0.5) is exactly 0). Between numbers of very different size the result is dominated by the
        /// larger one (Lerp(1, 1e1000, 0.5) is 5e999); for a "growing counter" animation interpolate the exponents
        /// instead: BigDouble.Pow10(BigDouble.Lerp(BigDouble.Log10(a), BigDouble.Log10(b), t)).
        /// </summary>
        public static BigDouble Lerp(BigDouble a, BigDouble b, double t)
        {
            if (t <= 0)
            {
                return a;
            }
            if (t >= 1)
            {
                return b;
            }
            var result = LerpUnclamped(a, b, t);
            // rounding must not leave the interval between the end points
            var low = a.lt(b) ? a : b;
            var high = a.lt(b) ? b : a;
            return result.lt(low) ? low : result.gt(high) ? high : result;
        }

        /// <summary>a + (b - a) * t without clamping t (t = 2 extrapolates to 2b - a). NaN for t = NaN.</summary>
        public static BigDouble LerpUnclamped(BigDouble a, BigDouble b, double t)
        {
            if (t == 0)
            {
                return a;
            }
            if (t == 1)
            {
                return b;
            }
            // Towers (layer 2 and up): a factor does not change them, so a + (b - a) * t cancels to 0 although the larger
            // end point decides the result.
            if (a.isFinite() && b.isFinite() && (a._layer >= 2 || b._layer >= 2))
            {
                if (a._layer == b._layer && a._mag == b._mag)
                {
                    return a._sign == b._sign ? a : b.mul(2 * t - 1); // exactly opposite: (1 - t) * a + t * b = b * (2t - 1)
                }
                return a.mul(1 - t).add(b.mul(t));
            }
            // End points of opposite sign: between them the result passes through zero, where a + (b - a) * t cancels.
            // b - a is larger than both end points (from 9e15 on stored as a logarithm), so Lerp(-8e15, 8e15, 0.5)
            // gave 3 instead of 0.
            if (t > 0 && t < 1 && a._sign * b._sign < 0 && a.isFinite() && b.isFinite())
            {
                return LerpAcrossZero(a, b, t);
            }
            // Computed from the end point nearer to t: a + (b - a) * t near t = 0 and b - (b - a) * (1 - t) near t = 1.
            // With a + (b - a) * t alone, t close to 1 subtracts two nearly equal large numbers and loses the result
            // (Lerp(1e1000, 1, 0.99999999999999) was 0 instead of 9.99e985). 1 - t is exact for t >= 0.5.
            var difference = b.sub(a);
            return t <= 0.5 ? a.add(difference.mul(t)) : b.sub(difference.mul(1 - t));
        }

        // Lerp for finite end points of opposite sign and 0 < t < 1, accurate where the result passes through zero.
        // Nothing is rounded to zero: a result is 0 only if the computation gives exactly 0.
        private static BigDouble LerpAcrossZero(BigDouble a, BigDouble b, double t)
        {
            const double safeDoubleLog = 290; // |x| in [1e-290, 1e290]: plain doubles, no overflow in TwoProduct
            bool aIsDouble = a._layer == 0 || (a._layer == 1 && Math.Abs(a._mag) <= safeDoubleLog);
            bool bIsDouble = b._layer == 0 || (b._layer == 1 && Math.Abs(b._mag) <= safeDoubleLog);
            if ((a._layer == 0 || b._layer == 0) && aIsDouble && bIsDouble)
            {
                // An ordinary end point (layer 0 values are exact doubles; a layer 1 end point is converted with
                // about one ulp).
                return fromNumber(LerpDoubles(a.toNumber(), b.toNumber(), t));
            }
            if (a._layer == 1 && b._layer == 1)
            {
                // (1 - t) * a + t * b = sign(L) * 10^log|L| * (wL - wS * 10^(log|S| - log|L|)), with L the end point of
                // larger magnitude, S the other one and wL, wS their weights. The logarithms are the stored values, so
                // for nearly opposite end points their difference is exact; there 10^d = 1 + Expm1(d * ln 10) and
                // wL - wS = +-(2t - 1) is exact for t >= 0.25. The bracket is a plain double, so a result near zero
                // keeps double precision instead of the logarithmic precision of the end points.
                bool bIsLarger = b._mag >= a._mag;
                var large = bIsLarger ? b : a;
                double weightSmall = bIsLarger ? 1 - t : t;
                double logRatio = (bIsLarger ? a._mag : b._mag) - large._mag;
                double bracket = logRatio > -0.15
                    ? (bIsLarger ? 2 * t - 1 : 1 - 2 * t) - weightSmall * Expm1(logRatio * 2.302585092994046)
                    : (bIsLarger ? t : 1 - t) - weightSmall * Math.Pow(10, logRatio);
                if (bracket == 0)
                {
                    return dZero;
                }
                double log = large._mag + Math.Log10(Math.Abs(bracket));
                // Inside the double range one multiplication is more precise than the detour over the logarithm.
                return Math.Abs(large._mag) <= safeDoubleLog && Math.Abs(log) <= safeDoubleLog
                    ? fromNumber(large._sign * Math.Pow(10, large._mag) * bracket)
                    : FC(large._sign * Math.Sign(bracket), 1, log);
            }
            // A layer 0 end point and one beyond 1e290: zero is crossed within 1e-274 of the small end point, which the
            // nearer-end-point formula already starts from.
            var difference = b.sub(a);
            return t <= 0.5 ? a.add(difference.mul(t)) : b.sub(difference.mul(1 - t));
        }

        // x + t * (y - x) to within one ulp, however many digits cancel. Its exact value is the sum of five doubles:
        // x and the error-free parts of y - x and of both products with t. Two error-free summation passes (Rump's
        // SumK with K = 3) leave an error of about 1e-45 of the end points, far below the result's last digit.
        private static double LerpDoubles(double x, double y, double t)
        {
            double differenceHigh = TwoSum(y, -x, out double differenceLow);
            double p3 = TwoProduct(t, differenceHigh, out double p2);
            double p1 = TwoProduct(t, differenceLow, out double p0);
            double p4 = x;
            for (int pass = 0; pass < 2; pass++)
            {
                p1 = TwoSum(p1, p0, out p0);
                p2 = TwoSum(p2, p1, out p1);
                p3 = TwoSum(p3, p2, out p2);
                p4 = TwoSum(p4, p3, out p3);
            }
            return (((p0 + p1) + p2) + p3) + p4;
        }

        // x + y == sum + error exactly (Knuth's TwoSum).
        private static double TwoSum(double x, double y, out double error)
        {
            double sum = x + y;
            double yPart = sum - x;
            error = (x - (sum - yPart)) + (y - yPart);
            return sum;
        }

        // x * y == product + error exactly (Dekker's TwoProduct; .NET Standard 2.0 has no FusedMultiplyAdd).
        // Valid for |x|, |y| below about 1e300; products below about 1e-290 make the error inexact but tiny.
        private static double TwoProduct(double x, double y, out double error)
        {
            double product = x * y;
            SplitDouble(x, out double xHigh, out double xLow);
            SplitDouble(y, out double yHigh, out double yLow);
            error = ((xHigh * yHigh - product) + xHigh * yLow + xLow * yHigh) + xLow * yLow;
            return product;
        }

        // Veltkamp split: x == high + low with at most 26 significant bits each.
        private static void SplitDouble(double x, out double high, out double low)
        {
            double scaled = 134217729.0 * x; // 2^27 + 1
            high = scaled - (scaled - x);
            low = x - high;
        }

        /// <summary>
        /// The t in [0, 1] for which Lerp(a, b, t) is value, like Mathf.InverseLerp: (value - a) / (b - a), clamped.
        /// 0 if a == b; NaN if any argument is NaN.
        /// </summary>
        public static double InverseLerp(BigDouble a, BigDouble b, BigDouble value)
        {
            if (a.isNan() || b.isNan() || value.isNan())
            {
                return double.NaN;
            }
            if (a.eq(b))
            {
                return 0;
            }
            double t = value.sub(a).div(b.sub(a)).toNumber();
            return double.IsNaN(t) ? 0 : t <= 0 ? 0 : t >= 1 ? 1 : t;
        }

        /// <summary>
        /// True if |a - b| &lt;= tolerance * max(|a|, |b|): a relative tolerance of the values (default 1e-7) on every
        /// layer. Unlike <see cref="eqTolerance(BigDouble, double)"/>, 1e1000 and 1.0001e1000 are NOT equal at 1e-7.
        /// The result can only be as precise as the stored values (at 1e1000000000 the value itself has ~7 digits).
        /// NaN is never equal; an infinity only equals the same infinity.
        /// </summary>
        public static bool ApproximatelyEquals(BigDouble a, BigDouble b, double tolerance = 1e-7)
        {
            if (a.isNan() || b.isNan() || double.IsNaN(tolerance))
            {
                return false;
            }
            if (a.eq(b))
            {
                return true;
            }
            if (!a.isFinite() || !b.isFinite())
            {
                return false;
            }
            var absA = a.abs();
            var absB = b.abs();
            bool aLarger = absA.gte(absB);
            var larger = aLarger ? absA : absB;
            var smaller = aLarger ? absB : absA;
            // ratio = |smaller| / |larger| in [0, 1]; |a - b| / max(|a|, |b|) is 1 - ratio for equal signs, 1 + ratio otherwise.
            double ratio = smaller.div(larger).toNumber();
            double relativeDifference = a._sign == b._sign ? 1 - ratio : 1 + ratio;
            return relativeDifference <= tolerance;
        }

        /// <summary>Rounds to the nearest integer; halves are rounded away from zero.</summary>
        public static BigDouble Round(BigDouble value)
        {
            return value.round();
        }

        /// <summary>
        /// Rounds to 'digits' decimal places (0 to 15); halves are rounded away from zero like <see cref="Round(BigDouble)"/>
        /// (Math.Round(double, int) rounds halves to even - use the overload with MidpointRounding for that).
        /// </summary>
        public static BigDouble Round(BigDouble value, int digits)
        {
            return Round(value, digits, MidpointRounding.AwayFromZero);
        }

        /// <summary>Rounds to an integer with the given rule (see <see cref="Round(BigDouble, int, MidpointRounding)"/>).</summary>
        public static BigDouble Round(BigDouble value, MidpointRounding mode)
        {
            return Round(value, 0, mode);
        }

        /// <summary>
        /// Rounds to 'digits' decimal places (0 to 15, like Math.Round) with the given rule: ToEven and AwayFromZero
        /// decide halves, and where the runtime offers them ToZero, ToNegativeInfinity and ToPositiveInfinity round in
        /// that direction. As for Math.Round, the exact stored value decides: 2.675 is stored as 2.67499999..., so
        /// Round(2.675, 2) is 2.67. Values from 2^52 on (and everything beyond layer 0) are already whole numbers and
        /// are returned unchanged; NaN and ±Infinity too.
        /// </summary>
        public static BigDouble Round(BigDouble value, int digits, MidpointRounding mode)
        {
            if (digits < 0 || digits > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(digits), "digits must be between 0 and 15.");
            }
            int rule = (int)mode; // 0 ToEven, 1 AwayFromZero, 2 ToZero, 3 ToNegativeInfinity, 4 ToPositiveInfinity
            if (rule < 0 || rule > 4)
            {
                throw new ArgumentException("Unknown MidpointRounding value " + rule + ".", nameof(mode));
            }
            if (!value.isFinite() || value._sign == 0 || (value._layer >= 1 && value._mag > 0))
            {
                return value;
            }
            double x = value.toNumber(); // layer 0, or a tiny layer 1 value (|x| < 1.1e-16, possibly 0 as a double)
            if (Math.Abs(x) >= 4503599627370496)
            {
                return value;
            }
            var scale = System.Numerics.BigInteger.Pow(10, digits);
            ExactRational(Math.Abs(x), out var num, out var den);
            var scaled = num * scale;
            var whole = System.Numerics.BigInteger.DivRem(scaled, den, out var remainder);
            int half = (2 * remainder).CompareTo(den); // < 0 below the half, 0 exactly half, > 0 above
            bool positive = value._sign > 0;
            bool up; // away from zero
            switch (rule)
            {
                case 0:
                    up = half > 0 || (half == 0 && !whole.IsEven);
                    break;
                case 1:
                    up = half >= 0;
                    break;
                case 2:
                    up = false;
                    break;
                case 3:
                    up = !positive && !remainder.IsZero;
                    break;
                default:
                    up = positive && !remainder.IsZero;
                    break;
            }
            if (x == 0)
            {
                // a non-zero value below the double range: only a directed rounding away from zero leaves 0
                up = (rule == 3 && !positive) || (rule == 4 && positive);
            }
            if (up)
            {
                whole += 1;
            }
            if (whole.IsZero)
            {
                return dZero;
            }
            string digitsText = whole.ToString(CultureInfo.InvariantCulture);
            if (digits > 0)
            {
                if (digitsText.Length <= digits)
                {
                    digitsText = new string('0', digits + 1 - digitsText.Length) + digitsText;
                }
                digitsText = digitsText.Substring(0, digitsText.Length - digits) + "." + digitsText.Substring(digitsText.Length - digits);
            }
            double rounded = ParseInvariant(digitsText); // correctly rounded to the nearest double
            return fromNumber(positive ? rounded : -rounded);
        }

        /// <summary>Largest integer &lt;= value.</summary>
        public static BigDouble Floor(BigDouble value)
        {
            return value.floor();
        }

        /// <summary>Smallest integer &gt;= value.</summary>
        public static BigDouble Ceiling(BigDouble value)
        {
            return value.ceil();
        }

        /// <summary>Integer part (rounds towards zero).</summary>
        public static BigDouble Truncate(BigDouble value)
        {
            return value.trunc();
        }

        /// <summary>x ^ y.</summary>
        public static BigDouble Pow(BigDouble x, BigDouble y)
        {
            return x.pow(y);
        }

        /// <summary>10 ^ value.</summary>
        public static BigDouble Pow10(BigDouble value)
        {
            return value.pow10();
        }

        /// <summary>e ^ value.</summary>
        public static BigDouble Exp(BigDouble value)
        {
            return value.exp();
        }

        /// <summary>value * value.</summary>
        public static BigDouble Square(BigDouble value)
        {
            return value.sqr();
        }

        /// <summary>value ^ 3.</summary>
        public static BigDouble Cube(BigDouble value)
        {
            return value.cube();
        }

        /// <summary>Square root (NaN for negative values).</summary>
        public static BigDouble Sqrt(BigDouble value)
        {
            return value.sqrt();
        }

        /// <summary>Cube root (negative for negative values).</summary>
        public static BigDouble Cbrt(BigDouble value)
        {
            return value.cbrt();
        }

        /// <summary>The n-th root of value.</summary>
        public static BigDouble Root(BigDouble value, BigDouble n)
        {
            return value.root(n);
        }

        /// <summary>Base-10 logarithm (NaN for values &lt;= 0).</summary>
        public static BigDouble Log10(BigDouble value)
        {
            return value.log10();
        }

        /// <summary>Base-2 logarithm (NaN for values &lt;= 0).</summary>
        public static BigDouble Log2(BigDouble value)
        {
            return value.log2();
        }

        /// <summary>Natural logarithm (like Math.Log(x)).</summary>
        public static BigDouble Log(BigDouble value)
        {
            return value.ln();
        }

        /// <summary>Logarithm to the given base (like Math.Log(a, newBase)).</summary>
        public static BigDouble Log(BigDouble value, BigDouble newBase)
        {
            return value.log(newBase);
        }

        /// <summary>Natural logarithm.</summary>
        public static BigDouble Ln(BigDouble value)
        {
            return value.ln();
        }

        /// <summary>log10(|value|).</summary>
        public static BigDouble AbsLog10(BigDouble value)
        {
            return value.absLog10();
        }

        /// <summary>log10(value) for value &gt; 0, otherwise 0 (useful for prestige formulas).</summary>
        public static BigDouble PositiveLog10(BigDouble value)
        {
            return value.pLog10();
        }

        /// <summary>value! (gamma(value + 1) for non-integers).</summary>
        public static BigDouble Factorial(BigDouble value)
        {
            return value.factorial();
        }

        /// <summary>The gamma function.</summary>
        public static BigDouble Gamma(BigDouble value)
        {
            return value.gamma();
        }

        /// <summary>ln(gamma(value)).</summary>
        public static BigDouble LnGamma(BigDouble value)
        {
            return value.lngamma();
        }

        /// <summary>Tetration: value ^^ height (iterated exponentiation of payload if payload != 1).</summary>
        public static BigDouble Tetrate(BigDouble value, double height = 2, BigDouble? payload = null, bool linear = false)
        {
            return value.tetrate(height, payload, linear);
        }

        /// <summary>Iterated exponentiation (same as <see cref="Tetrate"/>).</summary>
        public static BigDouble IteratedExp(BigDouble value, double height = 2, BigDouble? payload = null, bool linear = false)
        {
            return value.tetrate(height, payload, linear);
        }

        /// <summary>Applies log(base) 'times' times (default base 10).</summary>
        public static BigDouble IteratedLog(BigDouble value, BigDouble? @base = null, double times = 1, bool linear = false)
        {
            return value.iteratedlog(@base, times, linear);
        }

        /// <summary>Super-logarithm (default base 10).</summary>
        public static BigDouble Slog(BigDouble value, BigDouble? @base = null, bool linear = false)
        {
            return value.slog(@base, 100, linear);
        }

        /// <summary>Adds diff to the slog(base) representation (default base 10).</summary>
        public static BigDouble LayerAdd(BigDouble value, double diff, BigDouble? @base = null, bool linear = false)
        {
            return value.layeradd(diff, @base, linear);
        }

        /// <summary>Adds diff to the slog10 representation (adds layers).</summary>
        public static BigDouble LayerAdd10(BigDouble value, BigDouble diff, bool linear = false)
        {
            return value.layeradd10(diff, linear);
        }

        /// <summary>Lambert W function (principal branch W0 by default, W_-1 if principal is false).</summary>
        public static BigDouble LambertW(BigDouble value, bool principal = true)
        {
            return value.lambertw(principal);
        }

        /// <summary>Super square root: x with x^^2 == value.</summary>
        public static BigDouble SuperSqrt(BigDouble value)
        {
            return value.ssqrt();
        }

        /// <summary>Super-root of the given degree (linear approximation of tetration).</summary>
        public static BigDouble LinearSuperRoot(BigDouble value, double degree)
        {
            return value.linear_sroot(degree);
        }

        /// <summary>Pentation: value ^^^ height.</summary>
        public static BigDouble Pentate(BigDouble value, double height = 2, BigDouble? payload = null, bool linear = false)
        {
            return value.pentate(height, payload, linear);
        }

        /// <summary>Penta-logarithm (default base 10).</summary>
        public static BigDouble PentaLog(BigDouble value, BigDouble? @base = null, bool linear = false)
        {
            return value.penta_log(@base, 100, linear);
        }

        /// <summary>Penta-root of the given degree (linear approximation of tetration).</summary>
        public static BigDouble LinearPentaRoot(BigDouble value, double degree)
        {
            return value.linear_penta_root(degree);
        }

        /// <summary>Sine.</summary>
        public static BigDouble Sin(BigDouble value)
        {
            return value.sin();
        }

        /// <summary>Cosine.</summary>
        public static BigDouble Cos(BigDouble value)
        {
            return value.cos();
        }

        /// <summary>Tangent.</summary>
        public static BigDouble Tan(BigDouble value)
        {
            return value.tan();
        }

        /// <summary>Arcsine.</summary>
        public static BigDouble Asin(BigDouble value)
        {
            return value.asin();
        }

        /// <summary>Arccosine.</summary>
        public static BigDouble Acos(BigDouble value)
        {
            return value.acos();
        }

        /// <summary>Arctangent.</summary>
        public static BigDouble Atan(BigDouble value)
        {
            return value.atan();
        }

        /// <summary>Hyperbolic sine.</summary>
        public static BigDouble Sinh(BigDouble value)
        {
            return value.sinh();
        }

        /// <summary>Hyperbolic cosine.</summary>
        public static BigDouble Cosh(BigDouble value)
        {
            return value.cosh();
        }

        /// <summary>Hyperbolic tangent.</summary>
        public static BigDouble Tanh(BigDouble value)
        {
            return value.tanh();
        }

        /// <summary>Inverse hyperbolic sine.</summary>
        public static BigDouble Asinh(BigDouble value)
        {
            return value.asinh();
        }

        /// <summary>Inverse hyperbolic cosine.</summary>
        public static BigDouble Acosh(BigDouble value)
        {
            return value.acosh();
        }

        /// <summary>Inverse hyperbolic tangent.</summary>
        public static BigDouble Atanh(BigDouble value)
        {
            return value.atanh();
        }

        /// <summary>How many items with geometrically increasing price can be bought (see break_eternity.js affordGeometricSeries).</summary>
        public static BigDouble AffordGeometricSeries(BigDouble resourcesAvailable, BigDouble priceStart, BigDouble priceRatio, BigDouble currentOwned)
        {
            return affordGeometricSeries(resourcesAvailable, priceStart, priceRatio, currentOwned);
        }

        /// <summary>Total cost of numItems items with geometrically increasing price.</summary>
        public static BigDouble SumGeometricSeries(BigDouble numItems, BigDouble priceStart, BigDouble priceRatio, BigDouble currentOwned)
        {
            return sumGeometricSeries(numItems, priceStart, priceRatio, currentOwned);
        }

        /// <summary>How many items with arithmetically increasing price can be bought.</summary>
        public static BigDouble AffordArithmeticSeries(BigDouble resourcesAvailable, BigDouble priceStart, BigDouble priceAdd, BigDouble currentOwned)
        {
            return affordArithmeticSeries(resourcesAvailable, priceStart, priceAdd, currentOwned);
        }

        /// <summary>Total cost of numItems items with arithmetically increasing price.</summary>
        public static BigDouble SumArithmeticSeries(BigDouble numItems, BigDouble priceStart, BigDouble priceAdd, BigDouble currentOwned)
        {
            return sumArithmeticSeries(numItems, priceStart, priceAdd, currentOwned);
        }

        /// <summary>Purchase efficiency score (lower is better).</summary>
        public static BigDouble EfficiencyOfPurchase(BigDouble cost, BigDouble currentRpS, BigDouble deltaRpS)
        {
            return efficiencyOfPurchase(cost, currentRpS, deltaRpS);
        }

        #endregion

        // ============================================================================================================
        // 3. Parsing and string conversion of BigDouble.
        //
        // Everything here is culture independent: the decimal separator is always '.', the output never depends on
        // CultureInfo.CurrentCulture, and double.Parse only ever sees validated, canonical text. Overflow is mapped to
        // ±Infinity by ParseInvariant, because Mono/.NET Framework throw OverflowException where .NET Core returns Infinity.
        //
        // ============================================================================================================

        // The default size of the LRU cache used to cache Parse/fromString.
        private const int DEFAULT_FROM_STRING_CACHE_SIZE = (1 << 10) - 1;

        private static class StringCache
        {
            public static readonly LRUCache<string, BigDouble> Instance = new LRUCache<string, BigDouble>(DEFAULT_FROM_STRING_CACHE_SIZE);
        }

        #region Parsing

        /// <summary>
        /// Parses a string. Throws <see cref="ArgumentNullException"/> for null and <see cref="FormatException"/>
        /// for invalid input. Accepted formats (case-insensitive, '.' is the decimal separator, ',' is ignored):
        /// <list type="bullet">
        /// <item><description>plain numbers: "123", "-1.5", ".5", "1.5e10", "1e1000000000" (any exponent size)</description></item>
        /// <item><description>"NaN", "Infinity", "-Infinity"</description></item>
        /// <item><description>exponent chains: "1e1e10" (10^10^10), "ee15.5", "-eee10", "2e3e10"</description></item>
        /// <item><description>layer notation (output of ToString/ToSaveString): "(e^7)1234.5", "-(e^7)1234.5"</description></item>
        /// <item><description>"X^Y" (power), "X^^Y" or "X^^Y;P" (tetration with payload P), "X^^^Y" (pentation)</description></item>
        /// <item><description>"XptY" / "XpY" (10^^X with payload Y), "(Y)F(X)" / "YFX"</description></item>
        /// </list>
        /// </summary>
        public static BigDouble Parse(string value)
        {
            return Parse(value, false);
        }

        /// <summary>
        /// Parses a string (see <see cref="Parse(string)"/>). If linearHyper4 is true, strings like "10^^8.5" use
        /// the linear approximation of tetration even for bases &lt;= 10.
        /// </summary>
        public static BigDouble Parse(string value, bool linearHyper4)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            if (!TryParseCore(value, linearHyper4, out var result))
            {
                throw new FormatException("'" + value + "' is not a valid BigDouble.");
            }
            return result;
        }

        /// <summary>Tries to parse a string (see <see cref="Parse(string)"/>). On failure, result is 0 and false is returned.</summary>
        public static bool TryParse(string value, out BigDouble result)
        {
            return TryParse(value, false, out result);
        }

        /// <summary>Tries to parse a string (see <see cref="Parse(string, bool)"/>). On failure, result is 0 and false is returned.</summary>
        public static bool TryParse(string value, bool linearHyper4, out BigDouble result)
        {
            if (value == null)
            {
                result = dZero;
                return false;
            }
            return TryParseCore(value, linearHyper4, out result);
        }

        /// <summary>
        /// Parses user input written in the given culture, like double.Parse(text, provider): the culture's decimal
        /// separator ("," in de-DE) is the decimal point, its group separator ("." in de-DE) is ignored, its signs and
        /// NaN/infinity symbols are understood. All formats of <see cref="Parse(string)"/> are accepted with the culture's
        /// separators ("1,5e1000" in de-DE). Parse("1,5", de-DE) is 1.5; Parse("1.5", de-DE) is 15, exactly as for
        /// double. Group separators are only accepted between digits of an integer part ("1.2,3" is invalid in en-US,
        /// as for double). The negative forms of the culture's "N" format ("(1,234.50)", "1,234.50-") are accepted too,
        /// so every text of ToString(format, provider) reads back. A null provider means the current culture. For save
        /// files keep using the culture independent <see cref="Parse(string)"/>, which never changes meaning.
        /// </summary>
        public static BigDouble Parse(string value, IFormatProvider provider)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            if (!TryParse(value, provider, out var result))
            {
                throw new FormatException("'" + value + "' is not a valid BigDouble in this culture.");
            }
            return result;
        }

        /// <summary>
        /// Tries to parse user input written in the given culture (see <see cref="Parse(string, IFormatProvider)"/>).
        /// On failure, result is 0 and false is returned.
        /// </summary>
        public static bool TryParse(string value, IFormatProvider provider, out BigDouble result)
        {
            result = dZero;
            if (value == null)
            {
                return false;
            }
            var info = NumberFormatInfo.GetInstance(provider);
            string text = value.Trim();
            if (text.Length == 0)
            {
                return false;
            }
            if (text == info.NaNSymbol)
            {
                result = dNaN;
                return true;
            }
            if (text == info.PositiveInfinitySymbol)
            {
                result = dInf;
                return true;
            }
            if (text == info.NegativeInfinitySymbol)
            {
                result = dNegInf;
                return true;
            }
            bool negated = TryStripNegativePattern(ref text, info);
            string invariant = ToInvariantNumberText(text, info);
            if (invariant == null || (negated && (invariant.Length == 0 || invariant[0] == '-' || invariant[0] == '+')))
            {
                return false;
            }
            if (!TryParseCore(invariant, false, out result))
            {
                result = dZero;
                return false;
            }
            if (negated)
            {
                result = result.neg();
            }
            return true;
        }

        // The negative forms that ToString("N", provider) writes for this culture besides "-n": "(n)", "- n", "n-", "n -".
        private static bool TryStripNegativePattern(ref string text, NumberFormatInfo info)
        {
            string sign = info.NegativeSign;
            switch (info.NumberNegativePattern)
            {
                case 0:
                    // "(1,234.50)"; not the BigDouble notations "(e^7)1234.5" and "(3)F(5)", which do not end in ')' or
                    // contain further parentheses
                    if (text.Length > 2 && text[0] == '(' && text[text.Length - 1] == ')' && text.IndexOf('(', 1) < 0 && text.IndexOf(')') == text.Length - 1)
                    {
                        text = text.Substring(1, text.Length - 2).Trim();
                        return true;
                    }
                    return false;
                case 2:
                    if (sign.Length > 0 && text.StartsWith(sign + " ", StringComparison.Ordinal))
                    {
                        text = text.Substring(sign.Length + 1);
                        return true;
                    }
                    return false;
                case 3:
                    if (sign.Length > 0 && text.Length > sign.Length && text.EndsWith(sign, StringComparison.Ordinal))
                    {
                        text = text.Substring(0, text.Length - sign.Length);
                        return true;
                    }
                    return false;
                case 4:
                    if (sign.Length > 0 && text.EndsWith(" " + sign, StringComparison.Ordinal))
                    {
                        text = text.Substring(0, text.Length - sign.Length - 1);
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        private const char NoBreakSpace = (char)0x00A0;
        private const char NarrowNoBreakSpace = (char)0x202F;

        // Rewrites culture specific number text with '.' as decimal point and without group separators. Null if the text
        // is invalid: group separators are only accepted between two digits of an integer part (not after the decimal
        // separator, not in an exponent), so "1.2,3" is rejected in en-US like double.Parse does.
        private static string ToInvariantNumberText(string text, NumberFormatInfo info)
        {
            string decimalSeparator = info.NumberDecimalSeparator;
            string groupSeparator = info.NumberGroupSeparator;
            // Cultures with a (narrow) no-break space as group separator also accept a normal space.
            bool spaceGroups = groupSeparator.Length == 1 && IsSpaceGroup(groupSeparator[0]);
            var sb = new StringBuilder(text.Length);
            bool decimalInNumber = false; // decimal separator seen since the last character that is not a digit
            bool exponentDigits = false; // the current digits belong to an exponent ("1e1000")
            char previous = '\0';
            int i = 0;
            while (i < text.Length)
            {
                int groupLength = 0;
                if (decimalSeparator.Length > 0 && string.CompareOrdinal(text, i, decimalSeparator, 0, decimalSeparator.Length) == 0)
                {
                    sb.Append('.');
                    i += decimalSeparator.Length;
                    decimalInNumber = true;
                    previous = '.';
                    continue;
                }
                if (groupSeparator.Length > 0 && string.CompareOrdinal(text, i, groupSeparator, 0, groupSeparator.Length) == 0)
                {
                    groupLength = groupSeparator.Length;
                }
                else if (spaceGroups && IsSpaceGroup(text[i]))
                {
                    groupLength = 1;
                }
                if (groupLength > 0)
                {
                    bool betweenDigits = previous >= '0' && previous <= '9' && i + groupLength < text.Length
                                         && text[i + groupLength] >= '0' && text[i + groupLength] <= '9';
                    if (!betweenDigits || decimalInNumber || exponentDigits)
                    {
                        return null;
                    }
                    i += groupLength;
                    continue;
                }

                char c;
                int length = 1;
                if (info.NegativeSign.Length > 0 && info.NegativeSign != "-" && string.CompareOrdinal(text, i, info.NegativeSign, 0, info.NegativeSign.Length) == 0)
                {
                    c = '-';
                    length = info.NegativeSign.Length;
                }
                else if (info.PositiveSign.Length > 0 && info.PositiveSign != "+" && string.CompareOrdinal(text, i, info.PositiveSign, 0, info.PositiveSign.Length) == 0)
                {
                    c = '+';
                    length = info.PositiveSign.Length;
                }
                else if (text[i] == ',' || text[i] == '.')
                {
                    // ',' or '.' that is neither decimal nor group separator here (both in fr-FR, which groups with a
                    // space): reject, as double does, instead of letting the culture independent parser read it
                    return null;
                }
                else
                {
                    c = text[i];
                }
                if (c < '0' || c > '9')
                {
                    decimalInNumber = false;
                    exponentDigits = c == 'e' || c == 'E' || ((c == '-' || c == '+') && (previous == 'e' || previous == 'E'));
                }
                sb.Append(c);
                previous = c;
                i += length;
            }
            return sb.ToString();
        }

        private static bool IsSpaceGroup(char c)
        {
            return c == ' ' || c == NoBreakSpace || c == NarrowNoBreakSpace;
        }

        /// <summary>break_eternity.js name of <see cref="Parse(string, bool)"/>. Throws <see cref="FormatException"/> on invalid input.</summary>
        public static BigDouble fromString(string value, bool linearhyper4 = false)
        {
            return Parse(value, linearhyper4);
        }

        /// <summary>Empties the cache used by Parse/TryParse (mainly useful for tests and memory measurements).</summary>
        public static void ClearParseCache()
        {
            StringCache.Instance.Clear();
        }

        private static bool TryParseCore(string value, bool linear, out BigDouble result)
        {
            // The cache only stores results of the default (analytic) mode, so linear parsing never sees them.
            if (!linear && StringCache.Instance.TryGetValue(value, out result))
            {
                return true;
            }
            if (!TryParseUncached(value, linear, out result))
            {
                result = dZero;
                return false;
            }
            if (!linear)
            {
                StringCache.Instance.Set(value, result);
            }
            return true;
        }

        private static bool TryParseUncached(string input, bool linear, out BigDouble result)
        {
            result = dZero;
            string value = input.Trim();
            if (value.IndexOf(',') >= 0)
            {
                value = value.Replace(",", ""); // break_eternity.js IGNORE_COMMAS (it only removed the first comma)
            }
            if (value.Length == 0)
            {
                return false;
            }
            string lower = value.ToLowerInvariant();

            switch (lower)
            {
                case "nan":
                    result = dNaN;
                    return true;
                case "infinity":
                case "+infinity":
                    result = dInf;
                    return true;
                case "-infinity":
                    result = dNegInf;
                    return true;
            }

            int caret = lower.IndexOf('^');
            if (caret >= 0)
            {
                //Handle x^^^y format.
                int idx = lower.IndexOf("^^^", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    if (idx != caret || lower.IndexOf('^', idx + 3) >= 0)
                    {
                        return false;
                    }
                    if (!TryParseHyperOperands(lower, idx, 3, out var pbase, out double pheight, out var ppayload))
                    {
                        return false;
                    }
                    result = pbase.pentate(pheight, ppayload, linear);
                    return true;
                }

                //Handle x^^y format.
                idx = lower.IndexOf("^^", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    if (idx != caret || lower.IndexOf('^', idx + 2) >= 0)
                    {
                        return false;
                    }
                    if (!TryParseHyperOperands(lower, idx, 2, out var tbase, out double theight, out var tpayload))
                    {
                        return false;
                    }
                    result = tbase.tetrate(theight, tpayload, linear);
                    return true;
                }

                //Handle (e^N)X format.
                if (StartsWithLayerNotation(lower))
                {
                    return TryParseLayerNotation(lower, linear, out result);
                }

                //Handle x^y format.
                if (lower.IndexOf('^', caret + 1) >= 0)
                {
                    return false;
                }
                if (!TryParseNumberToken(lower, 0, caret, true, out var powBase)
                    || !TryParseNumberToken(lower, caret + 1, lower.Length, true, out var powExponent))
                {
                    return false;
                }
                result = BigDouble.pow(powBase, powExponent);
                return true;
            }

            //handle X PT Y format.
            int sep = lower.IndexOf("pt", StringComparison.Ordinal);
            if (sep >= 0)
            {
                return TryParsePt(lower, sep, 2, linear, out result);
            }
            //handle XpY format (it's the same thing just with p).
            sep = lower.IndexOf('p');
            if (sep >= 0)
            {
                return TryParsePt(lower, sep, 1, linear, out result);
            }
            //handle XFY format
            sep = lower.IndexOf('f');
            if (sep >= 0)
            {
                return TryParseF(lower, sep, linear, out result);
            }

            return TryParseExponentChain(lower, out result);
        }

        // "AeB", "AeBeC", "eeeX", "-eeX" ... (break_eternity.js semantics)
        private static bool TryParseExponentChain(string lower, out BigDouble result)
        {
            result = dZero;
            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];
                if (!(IsDigit(c) || c == '.' || c == '+' || c == '-' || c == 'e'))
                {
                    return false;
                }
            }

            string[] parts = lower.Split('e');
            int ecount = parts.Length - 1;

            //Handle numbers that are plain floats (no e).
            if (ecount == 0)
            {
                return TryParseNumberToken(lower, 0, lower.Length, false, out result);
            }

            string first = parts[0];
            string last = parts[ecount];
            bool mantissaIsNumber = first.Length > 0 && first != "+" && first != "-";

            //Handle XeY: one exponent with an integer exponent part is an ordinary number in scientific notation.
            if (ecount == 1 && mantissaIsNumber && IsIntegerToken(last))
            {
                return TryParseNumberToken(lower, 0, lower.Length, true, out result);
            }

            if (!TryParseNumberToken(last, 0, last.Length, false, out _, out double exponent))
            {
                return false;
            }
            var mantissa = dOne;
            if (mantissaIsNumber && !TryParseNumberToken(first, 0, first.Length, false, out mantissa))
            {
                return false;
            }
            for (int i = 1; i < ecount; i++)
            {
                if (parts[i].Length != 0 && !TryParseNumberToken(parts[i], 0, parts[i].Length, false, out _))
                {
                    return false;
                }
            }

            if (mantissaIsNumber && mantissa._sign == 0)
            {
                result = dZero;
                return true;
            }


            //Handle XeY with a fractional exponent (e.g. "1e1.5" == 10^1.5)
            if (ecount == 1 && mantissaIsNumber)
            {
                //Example: 2e10 is equal to 10^log10(2e10) which is equal to 10^(10+log10(2))
                result = FC(mantissa._sign, 1, exponent + mantissa.absLog10().toNumber());
                return true;
            }

            //handle numbers like AeBeC and AeeeeBeC
            if (ecount >= 2)
            {
                string middle = parts[ecount - 1];
                if (middle.Length != 0)
                {
                    TryParseNumberToken(middle, 0, middle.Length, false, out _, out double me);
                    if (me == 0)
                    {
                        return false; // degenerate ("1e0e5"); break_eternity.js produces NaN here
                    }
                    if (IsFiniteD(me))
                    {
                        exponent *= JsSign(me);
                        exponent += f_maglog10(me);
                    }
                }
            }

            //Handle numbers written like eee... (N es) X
            if (!mantissaIsNumber)
            {
                result = FC(first == "-" ? -1 : 1, ecount, exponent);
                return true;
            }
            //Handle numbers written like Xeee... (N es) Y
            if (ecount == 2)
            {
                result = FC(1, 2, exponent).mul(mantissa);
                return true;
            }
            //at eee and above, mantissa is too small to be recognizable!
            result = FC(mantissa._sign, ecount, exponent);
            return true;
        }

        private static bool StartsWithLayerNotation(string lower)
        {
            int i = lower.Length > 0 && (lower[0] == '-' || lower[0] == '+') ? 1 : 0;
            return string.CompareOrdinal(lower, i, "(e^", 0, 3) == 0;
        }

        // "(e^N)X" and "-(e^N)X": sign, layer N and mag X.
        private static bool TryParseLayerNotation(string lower, bool linear, out BigDouble result)
        {
            result = dZero;
            int i = 0;
            double sign = 1;
            if (lower[0] == '-' || lower[0] == '+')
            {
                sign = lower[0] == '-' ? -1 : 1;
                i = 1;
            }
            int close = lower.IndexOf(')', i + 3);
            if (close < 0)
            {
                return false;
            }
            if (!TryParseNumberToken(lower, i + 3, close, true, out _, out double layer)
                || !TryParseNumberToken(lower, close + 1, lower.Length, true, out var magValue, out double magD))
            {
                return false;
            }
            if (layer >= 0 && layer == Math.Floor(layer) && IsFiniteD(magD))
            {
                result = FC(sign, layer, magD);
                return true;
            }
            // Handle cases like (e^-8)1 and (e^10.5)1 by calling tetrate
            result = BigDouble.tetrate(dTen, layer, magValue, linear);
            if (sign < 0)
            {
                result = result.neg();
            }
            return true;
        }

        // "base^^height" / "base^^height;payload" (also for ^^^)
        private static bool TryParseHyperOperands(string lower, int opIndex, int opLength, out BigDouble @base, out double height, out BigDouble payload)
        {
            height = 0;
            payload = dOne;
            if (!TryParseNumberToken(lower, 0, opIndex, true, out @base) || !@base.isFinite())
            {
                return false;
            }
            int heightStart = opIndex + opLength;
            int semicolon = lower.IndexOf(';', heightStart);
            int heightEnd = semicolon >= 0 ? semicolon : lower.Length;
            if (!TryParseNumberToken(lower, heightStart, heightEnd, true, out _, out height))
            {
                return false;
            }
            if (!IsFiniteD(height))
            {
                return false;
            }
            if (semicolon >= 0)
            {
                if (lower.IndexOf(';', semicolon + 1) >= 0
                    || !TryParseNumberToken(lower, semicolon + 1, lower.Length, true, out payload))
                {
                    return false;
                }
            }
            return true;
        }

        // "XptY" / "XpY" / "Xpt(Y)": 10^^X with payload Y (default 1). A leading '-' negates the result.
        private static bool TryParsePt(string lower, int sep, int sepLength, bool linear, out BigDouble result)
        {
            result = dZero;
            if (lower.IndexOf('p', sep + sepLength) >= 0)
            {
                return false;
            }
            int start = 0;
            bool negative = false;
            if (sep > 0 && (lower[0] == '-' || lower[0] == '+'))
            {
                negative = lower[0] == '-';
                start = 1;
            }
            if (start < sep && (lower[start] == '+' || lower[start] == '-'))
            {
                return false;
            }
            if (!TryParseNumberToken(lower, start, sep, true, out _, out double height))
            {
                return false;
            }
            if (!IsFiniteD(height))
            {
                return false;
            }
            if (!TryParseOptionalParenthesized(lower, sep + sepLength, lower.Length, true, out var payload))
            {
                return false;
            }
            result = BigDouble.tetrate(dTen, height, payload, linear);
            if (negative)
            {
                result = result.neg();
            }
            return true;
        }

        // "(Y)F(X)" / "YFX" / "FX": 10^^X with payload Y (default 1). A leading '-' negates the result.
        private static bool TryParseF(string lower, int sep, bool linear, out BigDouble result)
        {
            result = dZero;
            if (lower.IndexOf('f', sep + 1) >= 0)
            {
                return false;
            }
            int start = 0;
            bool negative = false;
            if (sep > 0 && (lower[0] == '-' || lower[0] == '+'))
            {
                negative = lower[0] == '-';
                start = 1;
            }
            if (!TryParseOptionalParenthesized(lower, start, sep, true, out var payload))
            {
                return false;
            }
            if (!TryParseOptionalParenthesized(lower, sep + 1, lower.Length, false, out var heightValue))
            {
                return false;
            }
            double height = heightValue.toNumber();
            if (!IsFiniteD(height))
            {
                return false;
            }
            result = BigDouble.tetrate(dTen, height, payload, linear);
            if (negative)
            {
                result = result.neg();
            }
            return true;
        }

        // "", "N" or "(N)". Empty is only allowed when allowEmpty is true and then means 1.
        private static bool TryParseOptionalParenthesized(string s, int start, int end, bool allowEmpty, out BigDouble value)
        {
            value = dOne;
            if (start == end)
            {
                return allowEmpty;
            }
            if (s[start] == '(')
            {
                if (end - start < 2 || s[end - 1] != ')')
                {
                    return false;
                }
                return TryParseNumberToken(s, start + 1, end - 1, true, out value);
            }
            return TryParseNumberToken(s, start, end, true, out value);
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        // [+-]?digits
        private static bool IsIntegerToken(string s)
        {
            int i = 0;
            if (s.Length > 0 && (s[0] == '+' || s[0] == '-'))
            {
                i = 1;
            }
            if (i == s.Length)
            {
                return false;
            }
            for (; i < s.Length; i++)
            {
                if (!IsDigit(s[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Parses s[start..end) as [+-]?(digits[.digits] | .digits)([eE][+-]?digits)? into a BigDouble.
        /// Works for any magnitude: values inside the double range are parsed exactly (correctly rounded),
        /// values outside it are built from their first 20 significant digits and the decimal exponent.
        /// </summary>
        private static bool TryParseNumberToken(string s, int start, int end, bool allowExponent, out BigDouble value)
        {
            return TryParseNumberToken(s, start, end, allowExponent, out value, out _);
        }

        /// <summary>
        /// Same token syntax, additionally returning the token as a plain double (like JavaScript's parseFloat):
        /// exact for the whole double range including subnormals, ±Infinity above it, ±0 below it.
        /// Layer counts and magnitudes are doubles in BigDouble, so they must be read this way to stay exact.
        /// </summary>
        private static bool TryParseNumberToken(string s, int start, int end, bool allowExponent, out BigDouble value, out double asDouble)
        {
            value = dZero;
            asDouble = 0;
            int i = start;
            bool negative = false;
            if (i < end && (s[i] == '+' || s[i] == '-'))
            {
                negative = s[i] == '-';
                i++;
            }
            int intStart = i;
            while (i < end && IsDigit(s[i]))
            {
                i++;
            }
            int intEnd = i;
            int fracStart = i;
            int fracEnd = i;
            if (i < end && s[i] == '.')
            {
                i++;
                fracStart = i;
                while (i < end && IsDigit(s[i]))
                {
                    i++;
                }
                fracEnd = i;
            }
            if (intEnd == intStart && fracEnd == fracStart)
            {
                return false;
            }
            double exp10 = 0;
            if (i < end && (s[i] == 'e' || s[i] == 'E'))
            {
                if (!allowExponent)
                {
                    return false;
                }
                i++;
                bool expNegative = false;
                if (i < end && (s[i] == '+' || s[i] == '-'))
                {
                    expNegative = s[i] == '-';
                    i++;
                }
                int expStart = i;
                while (i < end && IsDigit(s[i]))
                {
                    i++;
                }
                if (i == expStart)
                {
                    return false;
                }
                exp10 = DigitsToDouble(s, expStart, i);
                if (expNegative)
                {
                    exp10 = -exp10;
                }
            }
            if (i != end)
            {
                return false;
            }

            // Decimal exponent of the first significant digit.
            int firstSig = -1;
            double leadExp = 0;
            for (int k = intStart; k < intEnd; k++)
            {
                if (s[k] != '0')
                {
                    firstSig = k;
                    leadExp = intEnd - k - 1;
                    break;
                }
            }
            if (firstSig < 0)
            {
                for (int k = fracStart; k < fracEnd; k++)
                {
                    if (s[k] != '0')
                    {
                        firstSig = k;
                        leadExp = -(k - fracStart + 1);
                        break;
                    }
                }
            }
            if (firstSig < 0)
            {
                value = dZero;
                asDouble = negative ? -0.0 : 0.0;
                return true;
            }

            double totalExp = leadExp + exp10;
            var sb = new StringBuilder(32);
            double parsed;
            if (totalExp > NUMBER_EXP_MAX)
            {
                parsed = negative ? double.NegativeInfinity : double.PositiveInfinity;
            }
            else if (totalExp < NUMBER_EXP_MIN - 20)
            {
                parsed = negative ? -0.0 : 0.0;
            }
            else
            {
                // Canonical form "-123.456e-7" is understood by double.Parse on every runtime.
                if (negative)
                {
                    sb.Append('-');
                }
                if (intEnd > intStart)
                {
                    sb.Append(s, intStart, intEnd - intStart);
                }
                else
                {
                    sb.Append('0');
                }
                if (fracEnd > fracStart)
                {
                    sb.Append('.');
                    sb.Append(s, fracStart, fracEnd - fracStart);
                }
                if (exp10 != 0)
                {
                    sb.Append('e');
                    sb.Append(((long)exp10).ToString(CultureInfo.InvariantCulture));
                }
                // Values above double.MaxValue are only possible for exponent 308 and give ±Infinity here.
                parsed = ParseInvariant(sb.ToString());
                sb.Length = 0;
            }
            asDouble = parsed;

            // Values from 1e-307 up to double.MaxValue are taken as doubles. Smaller values lose precision as
            // subnormals (break_eternity.js uses the same 1e-307 limit), larger ones cannot be represented.
            if (totalExp >= -307 && IsFiniteD(parsed))
            {
                value = fromNumber(parsed);
                return true;
            }

            // Outside the double range: mantissa from the first 20 significant digits.
            int digitsEnd = fracEnd > fracStart ? fracEnd : intEnd;
            int taken = 0;
            for (int k = firstSig; k < digitsEnd && taken < 20; k++)
            {
                char c = s[k];
                if (c == '.')
                {
                    continue;
                }
                sb.Append(c);
                if (taken == 0)
                {
                    sb.Append('.');
                }
                taken++;
            }
            sb.Append('0');
            double mant = double.Parse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
            value = fromMantissaExponent(negative ? -mant : mant, totalExp);
            return true;
        }

        // Digit string to double without overflow exceptions (returns +Infinity for more than 300 digits).
        private static double DigitsToDouble(string s, int start, int end)
        {
            while (start < end - 1 && s[start] == '0')
            {
                start++;
            }
            if (end - start > 300)
            {
                return double.PositiveInfinity;
            }
            return double.Parse(s.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        #endregion

        #region String conversion

        /// <summary>
        /// Shortest text that parses back to exactly this value, like double.ToString(): plain number for ordinary layer 0
        /// values, "1.23e1000" for layer 1 (and very small/large layer 0 values), "ee15.95" for layers 2 to 5 and "(e^N)X"
        /// above. Culture independent and lossless (<c>BigDouble.Parse(x.ToString())</c> returns exactly x).
        /// break_eternity.js prints the digits of the logarithmic storage instead ("1.2300000000000255e1000" for
        /// 1.23e1000); <see cref="valueOf"/> and <see cref="toJSON"/> return that text. Layer 1 values need one or two
        /// trial parses (measured: under 1 µs on .NET 8, up to 5 µs in the Unity editor).
        /// </summary>
        public override readonly string ToString()
        {
            if (!isFinite())
            {
                return JsString();
            }
            if (_layer == 0)
            {
                // The shortest round-trip digits of the stored double; "1.5e+300" is written as "1.5e300".
                return FormatJsNumber(_sign * _mag).Replace("e+", "e");
            }
            if (_layer == 1)
            {
                return TryShortestMantissa(out string mantissa, out double exp)
                    ? mantissa + "e" + FormatJsNumber(exp)
                    : ToSaveString();
            }
            // Layer 2+ prints the stored mag with round-trip digits; checked because the parser normalises.
            string text = JsString();
            return TryParseUncached(text, false, out var back) && back.Equals(this) ? text : ToSaveString();
        }

        // break_eternity.js toString.
        private readonly string JsString()
        {
            if (isNan())
            {
                return "NaN";
            }
            if (double.IsPositiveInfinity(_mag) || double.IsPositiveInfinity(_layer))
            {
                return _sign == 1 ? "Infinity" : "-Infinity";
            }

            if (_layer == 0)
            {
                if ((_mag < 1e21 && _mag > 1e-7) || _mag == 0)
                {
                    return FormatJsNumber(_sign * _mag);
                }
                return MantissaExponentString();
            }
            if (_layer == 1)
            {
                return MantissaExponentString();
            }
            //layer 2+
            if (_layer <= MAX_ES_IN_A_ROW)
            {
                return (_sign == -1 ? "-" : "") + new string('e', (int)_layer) + FormatJsNumber(_mag);
            }
            return (_sign == -1 ? "-" : "") + "(e^" + FormatJsNumber(_layer) + ")" + FormatJsNumber(_mag);
        }

        private readonly string MantissaExponentString()
        {
            GetMantissaExponent(out double man, out double exp);
            return FormatJsNumber(man) + "e" + FormatJsNumber(exp);
        }

        /// <summary>
        /// Lossless, culture independent string for save files: <c>BigDouble.Parse(x.ToSaveString())</c> returns
        /// exactly the same components. Layer 0 values are written as plain numbers ("123.456", "1.5e-10"),
        /// larger values in layer notation ("(e^1)1000000000" == 1e1000000000).
        /// </summary>
        public readonly string ToSaveString()
        {
            if (isNan())
            {
                return "NaN";
            }
            if (!IsFiniteD(_layer) || !IsFiniteD(_mag))
            {
                return _sign < 0 ? "-Infinity" : "Infinity";
            }
            if (_layer == 0)
            {
                return FormatJsNumber(_sign * _mag);
            }
            return (_sign < 0 ? "-" : "") + "(e^" + FormatJsNumber(_layer) + ")" + FormatJsNumber(_mag);
        }

        /// <summary>Same as <see cref="ToString()"/>: the shortest text that parses back to exactly this value.</summary>
        public readonly string ToShortestString()
        {
            return ToString();
        }

        /// <summary>
        /// The text of break_eternity.js toString/valueOf, with all digits of the logarithmic storage
        /// ("1.2300000000000255e1000"). Not guaranteed to be lossless; <see cref="ToString()"/> is shorter and exact.
        /// </summary>
        public readonly string valueOf()
        {
            return JsString();
        }

        /// <summary>Same as <see cref="valueOf"/> (break_eternity.js name).</summary>
        public readonly string toJSON()
        {
            return JsString();
        }

        // Layer 1: the shortest mantissa text ("1.23") for which mantissa + "e" + exponent parses back to exactly this
        // value. False if no mantissa with up to 17 digits does. The mantissa is rounded to 1, 2, ... 17 digits; only
        // candidates whose logarithm comes within 2e-15 (relative, 9 to 18 ulps) of the stored one go through the parser,
        // which decides exactly. A candidate that parses back differs by at most about two ulps (two roundings in the
        // parser, one here); the margin only saves the parses of candidates that are clearly too far away.
        private readonly bool TryShortestMantissa(out string mantissa, out double exp)
        {
            GetMantissaExponent(out double man, out exp);
            double absMan = Math.Abs(man);
            string exponentText = "e" + (Math.Abs(exp) < 1e15 ? ((long)exp).ToString(CultureInfo.InvariantCulture) : FormatJsNumber(exp));
            double tolerance = Math.Abs(_mag) * 2e-15;
            double scale = 1;
            for (int digits = 1; digits <= 17; digits++, scale *= 10)
            {
                double scaled = Math.Round(absMan * scale, MidpointRounding.AwayFromZero);
                if (scaled >= 10 * scale || (digits > 1 && scaled % 10 == 0))
                {
                    continue; // rounding reached 10, or the same candidate as with fewer digits
                }
                if (Math.Abs(exp + Math.Log10(scaled / scale) - _mag) > tolerance)
                {
                    continue;
                }
                string all = ((long)scaled).ToString(CultureInfo.InvariantCulture);
                string candidate = (man < 0 ? "-" : "") + (digits == 1 ? all : all.Substring(0, 1) + "." + all.Substring(1));
                string text = candidate + exponentText;
                // A plain "d.ddde123" goes straight to the part of the parser that reads it.
                if (TryParseNumberToken(text, 0, text.Length, true, out var parsed) && parsed.Equals(this))
                {
                    mantissa = candidate;
                    return true;
                }
            }
            mantissa = null;
            return false;
        }

        #region Standard .NET formatting (IFormattable)

        /// <summary>
        /// Formats like double.ToString(format) with the current culture. See <see cref="ToString(string, IFormatProvider)"/>.
        /// </summary>
        public readonly string ToString(string format)
        {
            return ToString(format, null);
        }

        /// <summary>
        /// The general format "G" with the separators of the given culture. See <see cref="ToString(string, IFormatProvider)"/>.
        /// </summary>
        public readonly string ToString(IFormatProvider provider)
        {
            return ToString("G", provider);
        }

        /// <summary>
        /// Standard .NET number formats, so string.Format("{0:F2}", x), $"{x:N0}" and x.ToString("E3", culture) work like
        /// for double. Display only - save with <see cref="ToSaveString"/>.
        /// <list type="bullet">
        /// <item><description>"G" / "G5": shortest exact digits (or 5 significant digits); scientific notation from 10^15
        /// (or 10^precision) and below 10^-5, and always for values beyond the double-exact range (layer 1).</description></item>
        /// <item><description>"F2": fixed point with 2 decimals; "N2": the same with group separators ("1,234.50") and the
        /// culture's NumberNegativePattern for negative values ("(1,234.50)", "1,234.50-" ...). Values of 1e21 and more
        /// use the "E" format instead of printing digits that are not stored.</description></item>
        /// <item><description>"E3": d.dddE+006 like double; lower-case "e3" writes "e+006".</description></item>
        /// </list>
        /// Without precision, F and N use the culture's NumberDecimalDigits (usually 2) and E uses 6 decimals, like double.
        /// A null or empty format returns <see cref="ToString()"/> unchanged (so $"{x}" keeps the break_eternity notation);
        /// a null provider means the current culture. Midpoints are rounded away from zero on every runtime, decided by
        /// the exact stored value (2.5 with "F0" gives "3"; 2.675 with "F2" gives "2.67" because it is stored as
        /// 2.67499999...), and a result that rounds to zero never gets a minus sign. Values above 10^9e15 (layer 2 and
        /// more) ignore the precision and use the break_eternity notation ("ee20", "(e^7)1234.5") with the culture's
        /// decimal separator. Other formats (C, D, P, X, R, custom patterns like "0.00") throw <see cref="FormatException"/>.
        /// </summary>
        public readonly string ToString(string format, IFormatProvider provider)
        {
            if (string.IsNullOrEmpty(format))
            {
                return ToString();
            }
            char kind = char.ToUpperInvariant(format[0]);
            int precision = -1;
            if (format.Length > 1 && (!int.TryParse(format.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out precision) || precision > 100))
            {
                kind = '?';
            }
            if (kind != 'G' && kind != 'F' && kind != 'N' && kind != 'E')
            {
                throw new FormatException("BigDouble supports the formats G, F, N and E with an optional precision of 0 to 100, not '" + format + "'.");
            }
            var info = NumberFormatInfo.GetInstance(provider);
            if (isNan())
            {
                return info.NaNSymbol;
            }
            if (!isFinite())
            {
                return _sign < 0 ? info.NegativeInfinitySymbol : info.PositiveInfinitySymbol;
            }
            if (_layer >= 2)
            {
                string notation = ToString();
                return (notation[0] == '-' ? info.NegativeSign + notation.Substring(1) : notation).Replace(".", info.NumberDecimalSeparator);
            }
            bool lowerCase = format[0] == 'e' || format[0] == 'g';
            switch (kind)
            {
                case 'F':
                case 'N':
                    return FormatFixed(precision < 0 ? info.NumberDecimalDigits : precision, kind == 'N', info);
                case 'E':
                    return FormatExponential(precision < 0 ? 6 : precision, lowerCase ? 'e' : 'E', info);
                default:
                    return FormatGeneral(precision, lowerCase ? 'e' : 'E', info);
            }
        }

        private readonly string FormatFixed(int decimals, bool grouped, NumberFormatInfo info)
        {
            // "N" places the minus sign by the culture's NumberNegativePattern ("(1,234.50)", "1,234.50-" ...); "F" and
            // the other formats always prefix it, like double.
            int negativePattern = grouped ? info.NumberNegativePattern : 1;
            // Below 1e21 (layer 0 and the lower part of layer 1) the stored value is printed with its exact digits.
            double value = toNumber();
            if (Math.Abs(value) >= 1e21 || (_layer == 1 && _mag > 21))
            {
                return FormatExponential(decimals, 'E', info, negativePattern);
            }
            string digits = FixedDigits(Math.Abs(value), decimals);
            int point = digits.IndexOf('.');
            string integerPart = point >= 0 ? digits.Substring(0, point) : digits;
            string fraction = point >= 0 ? digits.Substring(point + 1) : "";
            if (grouped)
            {
                integerPart = GroupDigits(integerPart, info);
            }
            string text = fraction.Length > 0 ? integerPart + info.NumberDecimalSeparator + fraction : integerPart;
            return WithSign(text, digits, info, negativePattern);
        }

        private readonly string FormatExponential(int decimals, char exponentLetter, NumberFormatInfo info, int negativePattern = 1)
        {
            string digits;
            long exponent;
            if (_sign == 0)
            {
                digits = new string('0', decimals + 1);
                exponent = 0;
            }
            else if (_layer == 0)
            {
                digits = ExponentialDigits(_mag, decimals, out int e);
                exponent = e;
            }
            else
            {
                // layer 1: the mantissa is a double in [1, 10), the exponent an integer below 9e15
                GetMantissaExponent(out double man, out double exp);
                string mantissa = FixedDigits(Math.Abs(man), decimals);
                exponent = (long)exp;
                if (mantissa.StartsWith("10", StringComparison.Ordinal))
                {
                    mantissa = FixedDigits(Math.Abs(man) / 10, decimals); // rounded up to 10.00
                    exponent++;
                }
                digits = mantissa.Replace(".", "");
            }
            string text = digits.Substring(0, 1) + (decimals > 0 ? info.NumberDecimalSeparator + digits.Substring(1) : "")
                          + exponentLetter + (exponent < 0 ? info.NegativeSign : info.PositiveSign)
                          + Math.Abs(exponent).ToString("D3", CultureInfo.InvariantCulture);
            return WithSign(text, digits, info, negativePattern);
        }

        private readonly string FormatGeneral(int precision, char exponentLetter, NumberFormatInfo info)
        {
            if (_sign == 0)
            {
                return "0";
            }
            string digits;
            long exponent;
            if (_layer == 0)
            {
                if (precision <= 0)
                {
                    ShortestDigits(_mag, out digits, out int e);
                    exponent = e;
                }
                else
                {
                    digits = ExponentialDigits(_mag, precision - 1, out int e).TrimEnd('0');
                    exponent = e;
                }
            }
            else
            {
                GetMantissaExponent(out double man, out double exp);
                exponent = (long)exp;
                if (precision <= 0 && TryShortestMantissa(out string shortest, out _))
                {
                    digits = shortest.TrimStart('-').Replace(".", "");
                }
                else
                {
                    int significant = precision > 0 ? precision : 15;
                    string mantissa = FixedDigits(Math.Abs(man), significant - 1);
                    if (mantissa.StartsWith("10", StringComparison.Ordinal))
                    {
                        mantissa = FixedDigits(Math.Abs(man) / 10, significant - 1);
                        exponent++;
                    }
                    digits = mantissa.Replace(".", "").TrimEnd('0');
                }
            }
            if (digits.Length == 0)
            {
                digits = "0";
            }
            // like double: fixed point for exponents from -4 up to below the precision (15 without precision)
            int threshold = precision <= 0 ? 15 : precision;
            string text;
            if (_layer == 0 && exponent > -5 && exponent < threshold)
            {
                // fixed point: insert the decimal separator or pad with zeros
                int integerDigits = (int)exponent + 1;
                if (integerDigits <= 0)
                {
                    text = "0" + info.NumberDecimalSeparator + new string('0', -integerDigits) + digits;
                }
                else if (integerDigits >= digits.Length)
                {
                    text = digits + new string('0', integerDigits - digits.Length);
                }
                else
                {
                    text = digits.Substring(0, integerDigits) + info.NumberDecimalSeparator + digits.Substring(integerDigits);
                }
            }
            else
            {
                text = digits.Substring(0, 1) + (digits.Length > 1 ? info.NumberDecimalSeparator + digits.Substring(1) : "")
                       + exponentLetter + (exponent < 0 ? info.NegativeSign : info.PositiveSign)
                       + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
            }
            return WithSign(text, digits, info);
        }

        // Adds the culture's minus sign to negative values, unless every printed digit is zero. pattern is a
        // NumberNegativePattern: 0 "(n)", 1 "-n", 2 "- n", 3 "n-", 4 "n -".
        private readonly string WithSign(string text, string digits, NumberFormatInfo info, int pattern = 1)
        {
            if (_sign < 0)
            {
                foreach (char c in digits)
                {
                    if (c >= '1' && c <= '9')
                    {
                        switch (pattern)
                        {
                            case 0:
                                return "(" + text + ")";
                            case 2:
                                return info.NegativeSign + " " + text;
                            case 3:
                                return text + info.NegativeSign;
                            case 4:
                                return text + " " + info.NegativeSign;
                            default:
                                return info.NegativeSign + text;
                        }
                    }
                }
            }
            return text;
        }

        // Shortest round-trip digits of a positive double ("12345" and 2 for 123.45).
        private static void ShortestDigits(double absX, out string digits, out int exponent)
        {
            string r = ShortestRoundTrip(absX);
            int ePos = r.IndexOf('E');
            string mant = ePos >= 0 ? r.Substring(0, ePos) : r;
            int exp = ePos >= 0 ? int.Parse(r.Substring(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
            int dot = mant.IndexOf('.');
            string intPart = dot >= 0 ? mant.Substring(0, dot) : mant;
            string all = intPart + (dot >= 0 ? mant.Substring(dot + 1) : "");
            int pointPos = intPart.Length + exp;
            int lead = 0;
            while (lead < all.Length - 1 && all[lead] == '0')
            {
                lead++;
            }
            digits = all.Substring(lead).TrimEnd('0');
            exponent = pointPos - lead - 1;
        }

        // Inserts the culture's group separators into a string of integer digits (NumberGroupSizes, e.g. 3 or 3;2).
        private static string GroupDigits(string integerDigits, NumberFormatInfo info)
        {
            int[] sizes = info.NumberGroupSizes;
            if (sizes == null || sizes.Length == 0 || sizes[0] <= 0)
            {
                return integerDigits;
            }
            // Groups are taken from the right; the last size repeats, a size of 0 ends the grouping.
            string result = "";
            int end = integerDigits.Length;
            int index = 0;
            int size = sizes[0];
            while (end > size && size > 0)
            {
                result = info.NumberGroupSeparator + integerDigits.Substring(end - size, size) + result;
                end -= size;
                if (index < sizes.Length - 1)
                {
                    index++;
                    size = sizes[index];
                }
            }
            return integerDigits.Substring(0, end) + result;
        }

        #endregion

        /// <summary>Like JavaScript's Number.toExponential for layer 0, otherwise <see cref="toStringWithDecimalPlaces"/>.</summary>
        public readonly string toExponential(int places)
        {
            if (_layer == 0)
            {
                return JsToExponential(_sign * _mag, places);
            }
            return toStringWithDecimalPlaces(places);
        }

        /// <summary>Like JavaScript's Number.toFixed for layer 0, otherwise <see cref="toStringWithDecimalPlaces"/>.</summary>
        public readonly string toFixed(int places)
        {
            if (_layer == 0)
            {
                return JsToFixed(_sign * _mag, places);
            }
            return toStringWithDecimalPlaces(places);
        }

        /// <summary>Like JavaScript's Number.toPrecision.</summary>
        public readonly string toPrecision(int places)
        {
            double exp = e;
            // As in Number.prototype.toPrecision, the exponent after rounding decides: 99999.5 with 5 places is 1.0000e+5.
            if (_layer == 0 && _sign != 0 && places >= 1)
            {
                ExponentialDigits(_mag, places - 1, out int rounded);
                exp = rounded;
            }
            if (exp <= -7)
            {
                return toExponential(places - 1);
            }
            if (places > exp)
            {
                return toFixed((int)(places - exp - 1));
            }
            return toExponential(places - 1);
        }

        /// <summary>Like <see cref="ToString()"/>, but with the given number of decimal places.</summary>
        public readonly string toStringWithDecimalPlaces(int places)
        {
            if (_layer == 0)
            {
                if ((_mag < 1e21 && _mag > 1e-7) || _mag == 0)
                {
                    return JsToFixed(_sign * _mag, places);
                }
                GetMantissaExponent(out double man0, out double exp0);
                return FormatJsNumber(DecimalPlaces(man0, places)) + "e" + FormatJsNumber(DecimalPlaces(exp0, places));
            }
            if (_layer == 1)
            {
                GetMantissaExponent(out double man1, out double exp1);
                return FormatJsNumber(DecimalPlaces(man1, places)) + "e" + FormatJsNumber(DecimalPlaces(exp1, places));
            }
            //layer 2+
            if (_layer <= MAX_ES_IN_A_ROW)
            {
                return (_sign == -1 ? "-" : "") + new string('e', (int)_layer) + FormatJsNumber(DecimalPlaces(_mag, places));
            }
            return (_sign == -1 ? "-" : "") + "(e^" + FormatJsNumber(_layer) + ")" + FormatJsNumber(DecimalPlaces(_mag, places));
        }

        /// <summary>Mantissa rounded to the given number of decimal places.</summary>
        public readonly double mantissaWithDecimalPlaces(int places)
        {
            double man = m;
            // https://stackoverflow.com/a/37425022
            if (double.IsNaN(man))
            {
                return double.NaN;
            }
            if (man == 0)
            {
                return 0;
            }
            return DecimalPlaces(man, places);
        }

        /// <summary>Magnitude rounded to the given number of decimal places.</summary>
        public readonly double magnitudeWithDecimalPlaces(int places)
        {
            // https://stackoverflow.com/a/37425022
            if (double.IsNaN(_mag))
            {
                return double.NaN;
            }
            if (_mag == 0)
            {
                return 0;
            }
            return DecimalPlaces(_mag, places);
        }

        // break_eternity.js decimalPlaces(): rounds to 'places' significant decimals.
        private static double DecimalPlaces(double value, int places)
        {
            if (value == 0 || !IsFiniteD(value))
            {
                return value;
            }
            double len = places + 1;
            double numDigits = Math.Ceiling(Math.Log10(Math.Abs(value)));
            double rounded = JsRound(value * Math.Pow(10, len - numDigits)) * Math.Pow(10, numDigits - len);
            int fixedDigits = (int)Math.Min(Math.Max(len - numDigits, 0), 99);
            return ParseInvariant(JsToFixed(rounded, fixedDigits));
        }

        /// <summary>
        /// Formats a double exactly like JavaScript's Number.prototype.toString (shortest representation that
        /// round-trips, '.' as decimal separator, "1e+21" style exponents).
        /// </summary>
        internal static string FormatJsNumber(double x)
        {
            if (double.IsNaN(x))
            {
                return "NaN";
            }
            if (double.IsPositiveInfinity(x))
            {
                return "Infinity";
            }
            if (double.IsNegativeInfinity(x))
            {
                return "-Infinity";
            }
            if (x == 0)
            {
                return "0";
            }
            // Whole numbers below 2^53 print as they are.
            if (x == Math.Floor(x) && Math.Abs(x) < MAX_SAFE_INTEGER)
            {
                return ((long)x).ToString(CultureInfo.InvariantCulture);
            }

            string r = ShortestRoundTrip(x);
            // Without an exponent the "G" text already is the JavaScript text.
            if (r.IndexOf('E') < 0)
            {
                return r;
            }
            bool negative = r[0] == '-';
            int pos = negative ? 1 : 0;
            int ePos = r.IndexOf('E');
            string mant = ePos >= 0 ? r.Substring(pos, ePos - pos) : r.Substring(pos);
            int exp = ePos >= 0 ? int.Parse(r.Substring(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
            int dot = mant.IndexOf('.');
            string intPart = dot >= 0 ? mant.Substring(0, dot) : mant;
            string fracPart = dot >= 0 ? mant.Substring(dot + 1) : "";
            string digits = intPart + fracPart;
            int pointPos = intPart.Length + exp;
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0')
            {
                lead++;
            }
            digits = digits.Substring(lead).TrimEnd('0');
            pointPos -= lead;
            int k = digits.Length;
            int n = pointPos;

            var sb = new StringBuilder(32);
            if (negative)
            {
                sb.Append('-');
            }
            if (k <= n && n <= 21)
            {
                sb.Append(digits);
                sb.Append('0', n - k);
            }
            else if (0 < n && n <= 21)
            {
                sb.Append(digits, 0, n);
                sb.Append('.');
                sb.Append(digits, n, k - n);
            }
            else if (-6 < n && n <= 0)
            {
                sb.Append("0.");
                sb.Append('0', -n);
                sb.Append(digits);
            }
            else
            {
                int e10 = n - 1;
                sb.Append(digits[0]);
                if (k > 1)
                {
                    sb.Append('.');
                    sb.Append(digits, 1, k - 1);
                }
                sb.Append('e');
                sb.Append(e10 >= 0 ? '+' : '-');
                sb.Append(Math.Abs(e10).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // Shortest "G" representation that parses back to exactly x. Does not rely on the "R" format, which is not
        // reliable on .NET Framework/Mono.
        private static string ShortestRoundTrip(double x)
        {
            // Near double.MaxValue the shorter forms round up beyond the double range ("1.79769313486232E+308").
            string s = x.ToString("G15", CultureInfo.InvariantCulture);
            if (ParseInvariant(s) == x)
            {
                return s;
            }
            s = x.ToString("G16", CultureInfo.InvariantCulture);
            if (ParseInvariant(s) == x)
            {
                return s;
            }
            return x.ToString("G17", CultureInfo.InvariantCulture);
        }

        // double.Parse with the same result on every runtime: values beyond the double range give ±Infinity.
        // (.NET Core returns Infinity; .NET Framework and Unity's Mono throw OverflowException.)
        private static double ParseInvariant(string s)
        {
            try
            {
                return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                return s.Length > 0 && s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            }
        }

        // Number.prototype.toFixed and toExponential are computed from the exact binary value with BigInteger arithmetic,
        // as the ECMAScript specification describes, instead of delegating to double.ToString("F"/"E"): the runtimes
        // round exact midpoints differently (2.5.toFixed(0) is "3" in JavaScript and on Mono, "2" on .NET Core) and
        // disagree on the sign of negative values that round to zero.

        private static string JsToFixed(double x, int digits)
        {
            if (!IsFiniteD(x) || Math.Abs(x) >= 1e21)
            {
                return FormatJsNumber(x);
            }
            digits = Math.Min(Math.Max(digits, 0), 100);
            bool negative = x < 0; // -0 prints without a sign, -0.0001 as "-0.00" (like JavaScript)
            string m = FixedDigits(Math.Abs(x), digits);
            return negative ? "-" + m : m;
        }

        private static string JsToExponential(double x, int digits)
        {
            if (!IsFiniteD(x))
            {
                return FormatJsNumber(x);
            }
            digits = Math.Min(Math.Max(digits, 0), 100);
            if (x == 0)
            {
                return (digits > 0 ? "0." + new string('0', digits) : "0") + "e+0";
            }
            string m = ExponentialDigits(Math.Abs(x), digits, out int e);
            if (digits > 0)
            {
                m = m.Substring(0, 1) + "." + m.Substring(1);
            }
            return (x < 0 ? "-" : "") + m + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
        }

        // |x| rounded to 'decimals' decimal places from its exact value, ties away from zero: "123.45", "0.13".
        private static string FixedDigits(double absX, int decimals)
        {
            ExactRational(absX, out var num, out var den);
            // n = the integer closest to |x| * 10^decimals; ties go to the larger n
            var n = RoundHalfUp(num * System.Numerics.BigInteger.Pow(10, decimals), den);
            string m = n.ToString(CultureInfo.InvariantCulture);
            if (decimals > 0)
            {
                if (m.Length <= decimals)
                {
                    m = new string('0', decimals + 1 - m.Length) + m;
                }
                m = m.Substring(0, m.Length - decimals) + "." + m.Substring(m.Length - decimals);
            }
            return m;
        }

        // |x| > 0 rounded to fractionDigits + 1 significant digits from its exact value, ties away from zero.
        // Returns the digits without a decimal point; exponent is the power of ten of the first digit.
        private static string ExponentialDigits(double absX, int fractionDigits, out int exponent)
        {
            ExactRational(absX, out var num, out var den);
            // e with 10^e <= |x| < 10^(e+1), checked exactly (log10 is only the first guess)
            int e = (int)Math.Floor(Math.Log10(absX));
            while (CompareWithPowerOfTen(num, den, e) < 0)
            {
                e--;
            }
            while (CompareWithPowerOfTen(num, den, e + 1) >= 0)
            {
                e++;
            }
            // n = |x| / 10^(e - fractionDigits) rounded; 10^fractionDigits <= n <= 10^(fractionDigits + 1)
            int shift = fractionDigits - e;
            var n = shift >= 0
                ? RoundHalfUp(num * System.Numerics.BigInteger.Pow(10, shift), den)
                : RoundHalfUp(num, den * System.Numerics.BigInteger.Pow(10, -shift));
            string m = n.ToString(CultureInfo.InvariantCulture);
            if (m.Length > fractionDigits + 1)
            {
                m = m.Substring(0, fractionDigits + 1); // rounded up to 10^(fractionDigits + 1)
                e++;
            }
            exponent = e;
            return m;
        }

        // The exact value of a finite, non-negative double as num / den (den a power of two).
        private static void ExactRational(double x, out System.Numerics.BigInteger num, out System.Numerics.BigInteger den)
        {
            long bits = BitConverter.DoubleToInt64Bits(x);
            int exponent = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            if (exponent == 0)
            {
                exponent = 1; // subnormal
            }
            else
            {
                fraction |= 1L << 52;
            }
            exponent -= 1075;
            num = fraction;
            den = System.Numerics.BigInteger.One;
            if (exponent > 0)
            {
                num <<= exponent;
            }
            else
            {
                den <<= -exponent;
            }
        }

        // floor(num / den + 1/2) for non-negative values
        private static System.Numerics.BigInteger RoundHalfUp(System.Numerics.BigInteger num, System.Numerics.BigInteger den)
        {
            return System.Numerics.BigInteger.Divide(2 * num + den, 2 * den);
        }

        // Sign of num / den - 10^e
        private static int CompareWithPowerOfTen(System.Numerics.BigInteger num, System.Numerics.BigInteger den, int e)
        {
            return e >= 0
                ? num.CompareTo(den * System.Numerics.BigInteger.Pow(10, e))
                : (num * System.Numerics.BigInteger.Pow(10, -e)).CompareTo(den);
        }

        #endregion

        // ============================================================================================================
        // 4. Hyperoperators of BigDouble: tetration, iterated logarithm, super-logarithm, layeradd, Lambert W,
        // super-roots and pentation. Ported from break_eternity.js 2.1.3.
        //
        // Tetration for non-integer heights does not have a single agreed-upon definition, so this library uses an
        // analytic approximation for bases <= 10 and the linear approximation for bases > 10. Pass linear = true to
        // use the linear approximation for every base.
        //
        // ============================================================================================================

        // Safety net for bisection loops that only terminate once two consecutive guesses are identical.
        // A bisection over doubles converges after ~2100 steps, so this limit is never reached by valid input.
        private const int MAX_BISECTION_STEPS = 10000;

        #region Critical section tables

        //tetration/slog to real height stuff
        //background info and tables of values for critical functions taken here: https://github.com/Patashu/break_eternity.js/issues/22
        private static class Critical
        {
            public static readonly double[] Headers = { 2, Math.E, 3, 4, 5, 6, 7, 8, 9, 10 };

            public static readonly double[][] TetrValues =
            {
                new[]
                {
                    // Base 2 (using http://myweb.astate.edu/wpaulsen/tetcalc/tetcalc.html )
                    1, 1.0891180521811202527, 1.1789767925673958433, 1.2701455431742086633, 1.3632090180450091941,
                    1.4587818160364217007, 1.5575237916251418333, 1.6601571006859253673, 1.7674858188369780435, 1.8804192098842727359,
                    2,
                },
                new[]
                {
                    // Base E (using http://myweb.astate.edu/wpaulsen/tetcalc/tetcalc.html )
                    1, //0.0
                    1.1121114330934078681, //0.1
                    1.2310389249316089299, //0.2
                    1.3583836963111376089, //0.3
                    1.4960519303993531879, //0.4
                    1.6463542337511945810, //0.5
                    1.8121385357018724464, //0.6
                    1.9969713246183068478, //0.7
                    2.2053895545527544330, //0.8
                    2.4432574483385252544, //0.9
                    Math.E, //1.0
                },
                new[]
                {
                    // Base 3
                    1, 1.1187738849693603, 1.2464963939368214, 1.38527004705667, 1.5376664685821402,
                    1.7068895236551784, 1.897001227148399, 2.1132403089001035, 2.362480153784171,
                    2.6539010333870774, 3,
                },
                new[]
                {
                    // Base 4
                    1, 1.1367350847096405, 1.2889510672956703, 1.4606478703324786, 1.6570295196661111,
                    1.8850062585672889, 2.1539465047453485, 2.476829779693097, 2.872061932789197,
                    3.3664204535587183, 4,
                },
                new[]
                {
                    // Base 5
                    1, 1.1494592900767588, 1.319708228183931, 1.5166291280087583, 1.748171114438024,
                    2.0253263297298045, 2.3636668498288547, 2.7858359149579424, 3.3257226212448145,
                    4.035730287722532, 5,
                },
                new[]
                {
                    // Base 6
                    1, 1.159225940787673, 1.343712473580932, 1.5611293155111927, 1.8221199554561318,
                    2.14183924486326, 2.542468319282638, 3.0574682501653316, 3.7390572020926873, 4.6719550537360774,
                    6,
                },
                new[]
                {
                    // Base 7
                    1, 1.1670905356972596, 1.3632807444991446, 1.5979222279405536, 1.8842640123816674,
                    2.2416069644878687, 2.69893426559423, 3.3012632110403577, 4.121250340630164, 5.281493033448316,
                    7,
                },
                new[]
                {
                    // Base 8
                    1, 1.1736630594087796, 1.379783782386201, 1.6292821855668218, 1.9378971836180754,
                    2.3289975651071977, 2.8384347394720835, 3.5232708454565906, 4.478242031114584,
                    5.868592169644505, 8,
                },
                new[]
                {
                    // Base 9
                    1, 1.1793017514670474, 1.394054150657457, 1.65664127441059, 1.985170999970283,
                    2.4069682290577457, 2.9647310119960752, 3.7278665320924946, 4.814462547283592,
                    6.436522247411611, 9,
                },
                new[]
                {
                    // Base 10 (using http://myweb.astate.edu/wpaulsen/tetcalc/tetcalc.html )
                    1, 1.1840100246247336579, 1.4061375836156954169, 1.6802272208863963918, 2.026757028388618927,
                    2.4770056063449647580, 3.0805252717554819987, 3.9191964192627283911, 5.1351528408331864230,
                    6.9899611795347148455, 10,
                },
            };

            public static readonly double[][] SlogValues =
            {
                new[]
                {
                    // Base 2
                    -1, -0.9194161097107025, -0.8335625019330468, -0.7425599821143978, -0.6466611521029437,
                    -0.5462617907227869, -0.4419033816638769, -0.3342645487554494, -0.224140440909962,
                    -0.11241087890006762, 0,
                },
                new[]
                {
                    // Base E
                    -1, //0.0
                    -0.90603157029014, //0.1
                    -0.80786507256596, //0.2
                    -0.7064666939634, //0.3
                    -0.60294836853664, //0.4
                    -0.49849837513117, //0.5
                    -0.39430303318768, //0.6
                    -0.29147201034755, //0.7
                    -0.19097820800866, //0.8
                    -0.09361896280296, //0.9
                    0, //1.0
                },
                new[]
                {
                    // Base 3
                    -1, -0.9021579584316141, -0.8005762598234203, -0.6964780623319391, -0.5911906810998454,
                    -0.486050182576545, -0.3823089430815083, -0.28106046722897615, -0.1831906535795894,
                    -0.08935809204418144, 0,
                },
                new[]
                {
                    // Base 4
                    -1, -0.8917227442365535, -0.781258746326964, -0.6705130326902455, -0.5612813129406509,
                    -0.4551067709033134, -0.35319256652135966, -0.2563741554088552, -0.1651412821106526,
                    -0.0796919581982668, 0,
                },
                new[]
                {
                    // Base 5
                    -1, -0.8843387974366064, -0.7678744063886243, -0.6529563724510552, -0.5415870994657841,
                    -0.4352842206588936, -0.33504449124791424, -0.24138853420685147, -0.15445285440944467,
                    -0.07409659641336663, 0,
                },
                new[]
                {
                    // Base 6
                    -1, -0.8786709358426346, -0.7577735191184886, -0.6399546189952064, -0.527284921869926,
                    -0.4211627631006314, -0.3223479611761232, -0.23107655627789858, -0.1472057700818259,
                    -0.07035171210706326, 0,
                },
                new[]
                {
                    // Base 7
                    -1, -0.8740862815291583, -0.7497032990976209, -0.6297119746181752, -0.5161838335958787,
                    -0.41036238255751956, -0.31277212146489963, -0.2233976621705518, -0.1418697367979619,
                    -0.06762117662323441, 0,
                },
                new[]
                {
                    // Base 8
                    -1, -0.8702632331800649, -0.7430366914122081, -0.6213373075161548, -0.5072025698095242,
                    -0.40171437727184167, -0.30517930701410456, -0.21736343968190863, -0.137710238299109,
                    -0.06550774483471955, 0,
                },
                new[]
                {
                    // Base 9
                    -1, -0.8670016295947213, -0.7373984232432306, -0.6143173985094293, -0.49973884395492807,
                    -0.394584953527678, -0.2989649949848695, -0.21245647317021688, -0.13434688362382652,
                    -0.0638072667348083, 0,
                },
                new[]
                {
                    // Base 10
                    -1, -0.8641642839543857, -0.732534623168535, -0.6083127477059322, -0.4934049257184696,
                    -0.3885773075899922, -0.29376029055315767, -0.2083678561173622, -0.13155653399373268,
                    -0.062401588652553186, 0,
                },
            };
        }

        #endregion

        #region Tetration and iterated logarithm

        /// <summary>
        /// Tetration: this ^^ height (exponentiating this to this, height times). If payload != 1, this is iterated
        /// exponentiation: payload is exponentiated to base this, height times. Works with negative and real heights.
        /// </summary>
        public readonly BigDouble tetrate(double height = 2, BigDouble? payload = null, bool linear = false)
        {
            BigDouble p = payload ?? dOne;

            if (double.IsNaN(height))
            {
                return dNaN;
            }
            //x^^1 == x
            if (height == 1)
            {
                return BigDouble.pow(this, p);
            }
            //x^^0 == 1
            if (height == 0)
            {
                return p;
            }
            //1^^x == 1
            if (eq(dOne))
            {
                return dOne;
            }
            //-1^^x == -1
            if (eq(dNegOne))
            {
                return BigDouble.pow(this, p);
            }

            if (double.IsPositiveInfinity(height))
            {
                double this_num = toNumber();
                //within the convergence range?
                if (this_num <= TETRATION_CONVERGENCE_MAX && this_num >= TETRATION_CONVERGENCE_MIN)
                {
                    var negln = BigDouble.ln(this).neg();
                    //For bases above 1, b^x = x has two solutions. The lower solution is a stable equilibrium, the upper solution is an unstable equilibrium.
                    var lower = negln.lambertw().div(negln);
                    // However, if the base is below 1, there's only the stable equilibrium solution.
                    if (this_num < 1)
                    {
                        return lower;
                    }
                    var upper = negln.lambertw(false).div(negln);
                    //hotfix for the very edge of the number range not being handled properly
                    if (this_num > 1.444667861009099)
                    {
                        lower = upper = fromNumber(Math.E);
                    }
                    if (p.eq(upper))
                    {
                        return upper;
                    }
                    if (p.lt(upper))
                    {
                        return lower;
                    }
                    return dInf;
                }
                if (this_num > TETRATION_CONVERGENCE_MAX)
                {
                    //explodes to infinity
                    return dInf;
                }
                //0.06598803584531253708 > this_num >= 0: never converges
                //this_num < 0: quickly becomes a complex number
                return dNaN;
            }

            //0^^x oscillates if we define 0^0 == 1 (which we do), since then 0^^1 is 0, 0^^2 is 1, 0^^3 is 0, etc. payload is ignored
            //using the linear approximation for height
            if (eq(dZero))
            {
                double result = Math.Abs((height + 1) % 2);
                if (result > 1)
                {
                    result = 2 - result;
                }
                return fromNumber(result);
            }

            if (height < 0)
            {
                return BigDouble.iteratedlog(p, this, -height, linear);
            }

            double oldheight = height;
            height = Math.Truncate(height);
            double fracheight = oldheight - height;

            if (gt(dZero) && (lt(dOne) || (lte(TETRATION_CONVERGENCE_MAX) && p.lte(BigDouble.ln(this).neg().lambertw(false).div(BigDouble.ln(this).neg()))))
                && (oldheight > 10000 || !linear))
            {
                //similar to 0^^n, flip-flops between two values, converging slowly (or if it's below 0.06598803584531253708, never).
                //So once again, the fractional part at the beginning will be a linear approximation
                double limitheight = Math.Min(10000, height);
                if (p.eq(dOne))
                {
                    p = pow(fracheight);
                }
                else if (lt(dOne))
                {
                    p = p.pow(1 - fracheight).mul(pow(p).pow(fracheight));
                }
                else
                {
                    p = p.layeradd(fracheight, this);
                }
                for (int i = 0; i < limitheight; ++i)
                {
                    var old_payload = p;
                    p = pow(p);
                    //stop early if we converge
                    if (old_payload.eq(p))
                    {
                        return p;
                    }
                }
                if (oldheight > 10000 && Math.Ceiling(oldheight) % 2 == 1)
                {
                    return pow(p);
                }
                return p;
            }
            //TODO (from JS): base < 0, but it's hard to reason about (probably all non-integer heights are NaN automatically?)

            if (fracheight != 0)
            {
                if (p.eq(dOne))
                {
                    //If (linear), use linear approximation even for bases <= 10
                    //for bases above 10, revert to the linear approximation
                    if (gt(dTen) || linear)
                    {
                        p = pow(fracheight);
                    }
                    else
                    {
                        p = fromNumber(tetrate_critical(toNumber(), fracheight));
                        //until the critical section grid can handle numbers below 2, scale them to the base
                        if (lt(dTwo))
                        {
                            p = p.sub(dOne).mul(sub(dOne)).plus(dOne);
                        }
                    }
                }
                else
                {
                    if (eq(dTen))
                    {
                        p = p.layeradd10(fracheight, linear);
                    }
                    else if (lt(dOne))
                    {
                        p = p.pow(1 - fracheight).mul(pow(p).pow(fracheight));
                    }
                    else
                    {
                        p = p.layeradd(fracheight, this, linear);
                    }
                }
            }

            for (int i = 0; i < height; ++i)
            {
                p = pow(p);
                //bail if we're NaN
                if (!IsFiniteD(p._layer) || !IsFiniteD(p._mag))
                {
                    return p.normalize();
                }
                //shortcut
                if (p._layer - _layer > 3)
                {
                    return FC_NN(p._sign, p._layer + (height - i - 1), p._mag);
                }
                //give up after 10000 iterations if nothing is happening
                if (i > 10000)
                {
                    return p;
                }
            }
            return p;
        }

        /// <summary>Iterated exponentiation (identical to <see cref="tetrate(double, BigDouble?, bool)"/>).</summary>
        public readonly BigDouble iteratedexp(double height = 2, BigDouble? payload = null, bool linear = false)
        {
            return tetrate(height, payload, linear);
        }

        /// <summary>
        /// Applies log(base) 'times' times in a row. Approximately subtracts 'times' from the number's slog
        /// representation. Equivalent to tetrating to a negative height.
        /// </summary>
        public readonly BigDouble iteratedlog(BigDouble? @base = null, double times = 1, bool linear = false)
        {
            BigDouble b = @base ?? dTen;
            if (times < 0)
            {
                return BigDouble.tetrate(b, -times, this, linear);
            }

            var result = this;
            double fulltimes = times;
            times = Math.Truncate(times);
            double fraction = fulltimes - times;
            if (result._layer - b._layer > 3)
            {
                double layerloss = Math.Min(times, result._layer - b._layer - 3);
                times -= layerloss;
                result._layer -= layerloss;
            }

            for (int i = 0; i < times; ++i)
            {
                result = result.log(b);
                //bail if we're NaN
                if (!IsFiniteD(result._layer) || !IsFiniteD(result._mag))
                {
                    return result.normalize();
                }
                //give up after 10000 iterations if nothing is happening
                if (i > 10000)
                {
                    return result;
                }
            }

            //handle fractional part
            if (fraction > 0 && fraction < 1)
            {
                // break_eternity.js: "I have no clue what a fractional times on a base below 1 should even mean, so
                // I'm not going to bother - just let it be NaN". It does not return NaN though, but recurses
                // slog -> tetrate -> iteratedlog -> layeradd -> slog with 100 iterations per level, which takes
                // minutes to hours. Return the intended NaN directly.
                if (b.lt(dOne))
                {
                    return dNaN;
                }
                if (b.eq(dTen))
                {
                    result = result.layeradd10(-fraction, linear);
                }
                else
                {
                    result = result.layeradd(-fraction, b, linear);
                }
            }

            return result;
        }

        #endregion

        #region Super-logarithm

        /// <summary>
        /// Super-logarithm: how high a power tower of 'base' must be to reach this value. Uses 'iterations'
        /// steps of a search (default 100), assuming tetration as the ground truth.
        /// </summary>
        public readonly BigDouble slog(BigDouble? @base = null, int iterations = 100, bool linear = false)
        {
            BigDouble b = @base ?? dTen;
            double step_size = 0.001;
            bool has_changed_directions_once = false;
            bool previously_rose = false;
            double result = slog_internal(b, linear).toNumber();
            for (int i = 1; i < iterations; ++i)
            {
                var new_decimal = b.tetrate(result, dOne, linear);
                bool currently_rose = new_decimal.gt(this);
                if (i > 1)
                {
                    if (previously_rose != currently_rose)
                    {
                        has_changed_directions_once = true;
                    }
                }
                previously_rose = currently_rose;
                if (has_changed_directions_once)
                {
                    step_size /= 2;
                }
                else
                {
                    step_size *= 2;
                }
                step_size = Math.Abs(step_size) * (currently_rose ? -1 : 1);
                result += step_size;
                if (step_size == 0)
                {
                    break;
                }
            }
            return fromNumber(result);
        }

        private readonly BigDouble slog_internal(BigDouble @base, bool linear)
        {
            //special cases:
            //slog base 0 or lower is NaN
            if (@base.lte(dZero))
            {
                return dNaN;
            }
            //slog base 1 is NaN
            if (@base.eq(dOne))
            {
                return dNaN;
            }
            //need to handle these small, wobbling bases specially
            if (@base.lt(dOne))
            {
                if (eq(dOne))
                {
                    return dZero;
                }
                if (eq(dZero))
                {
                    return dNegOne;
                }
                //0 < this < 1: ambiguous (happens multiple times)
                //this < 0: impossible (as far as I can tell)
                //this > 1: partially complex
                return dNaN;
            }
            //slog_n(0) is -1
            if (_mag < 0 || eq(dZero))
            {
                return dNegOne;
            }
            if (@base.lt(TETRATION_CONVERGENCE_MAX))
            {
                var negln = BigDouble.ln(@base).neg();
                var infTower = negln.lambertw().div(negln);
                if (eq(infTower))
                {
                    return dInf;
                }
                if (gt(infTower))
                {
                    return dNaN;
                }
            }

            double result = 0;
            var copy = this;
            if (copy._layer - @base._layer > 3)
            {
                double layerloss = copy._layer - @base._layer - 3;
                result += layerloss;
                copy._layer -= layerloss;
            }

            for (int i = 0; i < 100; ++i)
            {
                if (copy.lt(dZero))
                {
                    copy = BigDouble.pow(@base, copy);
                    result -= 1;
                }
                else if (copy.lte(dOne))
                {
                    if (linear)
                    {
                        return fromNumber(result + copy.toNumber() - 1);
                    }
                    return fromNumber(result + slog_critical(@base.toNumber(), copy.toNumber()));
                }
                else
                {
                    result += 1;
                    copy = BigDouble.log(copy, @base);
                }
            }
            return fromNumber(result);
        }

        /// <summary>Interpolated slog value in the critical section (0 &lt; height &lt;= 1).</summary>
        public static double slog_critical(double @base, double height)
        {
            //for bases above 10, revert to old linear approximation
            if (@base > 10)
            {
                return height - 1;
            }
            return critical_section(@base, height, Critical.SlogValues);
        }

        /// <summary>Interpolated tetration value in the critical section (0 &lt;= height &lt;= 1).</summary>
        public static double tetrate_critical(double @base, double height)
        {
            return critical_section(@base, height, Critical.TetrValues);
        }

        private static double critical_section(double @base, double height, double[][] grid)
        {
            //this part is simple at least, since it's just 0.1 to 0.9
            height *= 10;
            if (height < 0)
            {
                height = 0;
            }
            if (height > 10)
            {
                height = 10;
            }
            //have to do this complicated song and dance since one of the critical_headers is Math.E
            if (@base < 2)
            {
                @base = 2;
            }
            if (@base > 10)
            {
                @base = 10;
            }
            double lower = 0;
            double upper = 0;
            if (double.IsNaN(height))
            {
                return double.NaN;
            }
            int lo = (int)Math.Floor(height);
            int hi = (int)Math.Ceiling(height);
            //basically, if we're between bases, we interpolate each bases' relevant values together
            //then we interpolate based on what the fractional height is.
            var headers = Critical.Headers;
            for (int i = 0; i < headers.Length; ++i)
            {
                if (headers[i] == @base)
                {
                    // exact match
                    lower = grid[i][lo];
                    upper = grid[i][hi];
                    break;
                }
                if (i + 1 < headers.Length && headers[i] < @base && headers[i + 1] > @base)
                {
                    // interpolate between this and the next
                    double basefrac = (@base - headers[i]) / (headers[i + 1] - headers[i]);
                    lower = grid[i][lo] * (1 - basefrac) + grid[i + 1][lo] * basefrac;
                    upper = grid[i][hi] * (1 - basefrac) + grid[i + 1][hi] * basefrac;
                    break;
                }
            }
            double frac = height - Math.Floor(height);
            //improvement - you get more accuracy (especially around 0.9-1.0) by doing log, then frac, then powing the result
            //also, use old linear for slog (values 0 or less in critical section).
            if (lower <= 0 || upper <= 0)
            {
                return lower * (1 - frac) + upper * frac;
            }
            return Math.Pow(@base, (Math.Log(lower) / Math.Log(@base)) * (1 - frac) + (Math.Log(upper) / Math.Log(@base)) * frac);
        }

        #endregion

        #region layeradd

        /// <summary>
        /// Adds/removes layers from a BigDouble, even fractional layers (like adding to its slog10 representation).
        /// Very similar to tetrate base 10 and iterated log base 10.
        /// </summary>
        public readonly BigDouble layeradd10(BigDouble diff, bool linear = false)
        {
            double d = diff.toNumber();
            var result = this;
            if (d >= 1)
            {
                //bug fix: if result is very smol (mag < 0, layer > 0) turn it into 0 first
                if (result._mag < 0 && result._layer > 0)
                {
                    result._sign = 0;
                    result._mag = 0;
                    result._layer = 0;
                }
                else if (result._sign == -1 && result._layer == 0)
                {
                    //bug fix - for stuff like -3.layeradd10(1) we need to move the sign to the mag
                    result._sign = 1;
                    result._mag = -result._mag;
                }
                double layeradd = Math.Truncate(d);
                d -= layeradd;
                result._layer += layeradd;
            }
            if (d <= -1)
            {
                double layeradd = Math.Truncate(d);
                d -= layeradd;
                result._layer += layeradd;
                if (result._layer < 0)
                {
                    for (int i = 0; i < 100; ++i)
                    {
                        result._layer++;
                        result._mag = Math.Log10(result._mag);
                        if (!IsFiniteD(result._mag))
                        {
                            //another bugfix: if we hit -Infinity mag, then we should return negative infinity, not 0. 0.layeradd10(-1) hits this
                            if (result._sign == 0)
                            {
                                result._sign = 1;
                            }
                            //also this, for 0.layeradd10(-2)
                            if (result._layer < 0)
                            {
                                result._layer = 0;
                            }
                            return result.normalize();
                        }
                        if (result._layer >= 0)
                        {
                            break;
                        }
                    }
                }
            }

            while (result._layer < 0)
            {
                result._layer++;
                result._mag = Math.Log10(result._mag);
            }
            //bugfix: before we normalize: if we started with 0, we now need to manually fix a layer ourselves!
            if (result._sign == 0)
            {
                result._sign = 1;
                if (result._mag == 0 && result._layer >= 1)
                {
                    result._layer -= 1;
                    result._mag = 1;
                }
            }
            result.NormalizeInPlace();

            //layeradd10: like adding 'diff' to the number's slog(base) representation.
            if (d != 0)
            {
                return result.layeradd(d, dTen, linear); //safe, only calls positive height 1 payload tetration, slog and log
            }

            return result;
        }

        /// <summary>
        /// Like adding 'diff' to the number's slog(base) representation. Very similar to tetrate base 'base'
        /// and iterated log base 'base'.
        /// </summary>
        public readonly BigDouble layeradd(double diff, BigDouble? @base = null, bool linear = false)
        {
            BigDouble baseD = @base ?? dTen;
            if (baseD.gt(dOne) && baseD.lte(TETRATION_CONVERGENCE_MAX))
            {
                var excessSlog = excess_slog(this, baseD, linear, out int range);
                double slogthis2 = excessSlog.toNumber();
                double slogdest2 = slogthis2 + diff;
                var negln = BigDouble.ln(baseD).neg();
                var lower = negln.lambertw().div(negln);
                var upper = negln.lambertw(false).div(negln);
                var slogzero = dOne;
                if (range == 1)
                {
                    slogzero = lower.mul(upper).sqrt();
                }
                else if (range == 2)
                {
                    slogzero = upper.mul(dTwo);
                }
                var slogone = baseD.pow(slogzero);
                double wholeheight = Math.Floor(slogdest2);
                double fracheight = slogdest2 - wholeheight;
                var towertop = slogzero.pow(1 - fracheight).mul(slogone.pow(fracheight));
                return BigDouble.tetrate(baseD, wholeheight, towertop, linear); //wholediff is a whole number so this is safe even if it ends up calling iteratedlog
            }
            double slogthis = this.slog(baseD, 100, linear).toNumber();
            double slogdest = slogthis + diff;
            if (slogdest >= 0)
            {
                return BigDouble.tetrate(baseD, slogdest, dOne, linear);
            }
            if (!IsFiniteD(slogdest))
            {
                return dNaN;
            }
            if (slogdest >= -1)
            {
                return BigDouble.log(BigDouble.tetrate(baseD, slogdest + 1, dOne, linear), baseD);
            }
            return BigDouble.log(BigDouble.log(BigDouble.tetrate(baseD, slogdest + 2, dOne, linear), baseD), baseD);
        }

        /// <summary>
        /// A strange version of slog for bases between 1 and e^1/e which can handle values above base^^Infinity.
        /// range: 0 = below the lower solution of b^x = x (normal slog), 1 = between the two solutions,
        /// 2 = above the upper solution. Only differences between two returned values are meaningful.
        /// </summary>
        private static BigDouble excess_slog(BigDouble value, BigDouble @base, bool linear, out int range)
        {
            var baseD = @base;
            double baseN = @base.toNumber();
            range = 0;
            if (baseN == 1 || baseN <= 0)
            {
                return dNaN;
            }
            if (baseN > TETRATION_CONVERGENCE_MAX)
            {
                return value.slog(baseD, 100, linear);
            }
            var negln = BigDouble.ln(baseD).neg();
            var lower = negln.lambertw().div(negln);
            var upper = dInf;
            if (baseN > 1)
            {
                upper = negln.lambertw(false).div(negln);
            }
            if (baseN > 1.444667861009099)
            {
                lower = upper = fromNumber(Math.E);
            }
            if (value.lt(lower))
            {
                return value.slog(baseD, 100, linear);
            }
            if (value.eq(lower))
            {
                return dInf;
            }
            if (value.eq(upper))
            {
                range = 2;
                return dNegInf;
            }
            if (value.gt(upper))
            {
                var slogzero = upper.mul(dTwo);
                var slogone = baseD.pow(slogzero);
                double estimate = 0;
                if (value.gte(slogzero) && value.lt(slogone))
                {
                    estimate = 0;
                }
                else if (value.gte(slogone))
                {
                    var payload = slogone;
                    estimate = 1;
                    int guard = 0;
                    while (payload.lt(value) && guard++ < MAX_BISECTION_STEPS)
                    {
                        payload = baseD.pow(payload);
                        estimate = estimate + 1;
                        if (payload._layer > 3)
                        {
                            double layersleft = Math.Floor(value._layer - payload._layer + 1);
                            payload = baseD.iteratedexp(layersleft, payload, linear);
                            estimate = estimate + layersleft;
                        }
                    }
                    if (payload.gt(value))
                    {
                        payload = payload.log(baseD);
                        estimate = estimate - 1;
                    }
                }
                else if (value.lt(slogzero))
                {
                    var payload = slogzero;
                    estimate = 0;
                    int guard = 0;
                    while (payload.gt(value) && guard++ < MAX_BISECTION_STEPS)
                    {
                        payload = payload.log(baseD);
                        estimate = estimate - 1;
                    }
                }
                double fracheight = 0;
                double tested;
                double step_size = 0.5;
                var towertop = slogzero;
                var guess = dZero;
                while (step_size > 1e-16)
                {
                    tested = fracheight + step_size;
                    towertop = slogzero.pow(1 - tested).mul(slogone.pow(tested)); //Weighted geometric average
                    guess = BigDouble.iteratedexp(baseD, estimate, towertop);
                    if (guess.eq(value))
                    {
                        range = 2;
                        return fromNumber(estimate + tested);
                    }
                    if (guess.lt(value))
                    {
                        fracheight += step_size;
                    }
                    step_size /= 2;
                }
                if (guess.neqTolerance(value, 1e-7))
                {
                    return dNaN;
                }
                range = 2;
                return fromNumber(estimate + fracheight);
            }
            if (value.lt(upper) && value.gt(lower))
            {
                var slogzero = lower.mul(upper).sqrt(); //Geometric mean of the two b^x = x solutions
                var slogone = baseD.pow(slogzero);
                double estimate = 0;
                if (value.lte(slogzero) && value.gt(slogone))
                {
                    estimate = 0;
                }
                else if (value.lte(slogone))
                {
                    var payload = slogone;
                    estimate = 1;
                    int guard = 0;
                    while (payload.gt(value) && guard++ < MAX_BISECTION_STEPS)
                    {
                        payload = baseD.pow(payload);
                        estimate = estimate + 1;
                    }
                    if (payload.lt(value))
                    {
                        payload = payload.log(baseD);
                        estimate = estimate - 1;
                    }
                }
                else if (value.gt(slogzero))
                {
                    var payload = slogzero;
                    estimate = 0;
                    int guard = 0;
                    while (payload.lt(value) && guard++ < MAX_BISECTION_STEPS)
                    {
                        payload = payload.log(baseD);
                        estimate = estimate - 1;
                    }
                }
                double fracheight = 0;
                double tested;
                double step_size = 0.5;
                var towertop = slogzero;
                var guess = dZero;
                while (step_size > 1e-16)
                {
                    tested = fracheight + step_size;
                    towertop = slogzero.pow(1 - tested).mul(slogone.pow(tested)); //Weighted geometric average
                    guess = BigDouble.iteratedexp(baseD, estimate, towertop);
                    if (guess.eq(value))
                    {
                        range = 1;
                        return fromNumber(estimate + tested);
                    }
                    if (guess.gt(value))
                    {
                        fracheight += step_size;
                    }
                    step_size /= 2;
                }
                if (guess.neqTolerance(value, 1e-7))
                {
                    return dNaN;
                }
                range = 1;
                return fromNumber(estimate + fracheight);
            }
            // break_eternity.js throws "Unhandled behavior in excess_slog" here (only reachable with NaN input).
            return dNaN;
        }

        #endregion

        #region Lambert W

        /// <summary>
        /// The Lambert W function (product logarithm): the solution of W(x) * e^W(x) = x.
        /// principal = true: branch W0 (x &gt;= -1/e). principal = false: branch W_-1 (-1/e &lt;= x &lt; 0).
        /// Returns NaN outside the domain or if the iteration does not converge.
        /// </summary>
        public readonly BigDouble lambertw(bool principal = true)
        {
            if (lt(-0.3678794411710499))
            {
                return dNaN; //complex
            }
            if (isNan())
            {
                return dNaN;
            }
            if (principal)
            {
                if (abs().lt(s_1em300))
                {
                    return this;
                }
                if (_mag < 0)
                {
                    return fromNumber(f_lambertw(toNumber()));
                }
                if (_layer == 0)
                {
                    return fromNumber(f_lambertw(_sign * _mag));
                }
                if (lt(s_eee15))
                {
                    return d_lambertw(this);
                }
                // Numbers this large would sometimes fail to converge using d_lambertw, and at this size this.ln() is close enough
                return ln();
            }
            if (_sign == 1)
            {
                return dNaN; //complex
            }
            if (_layer == 0)
            {
                return fromNumber(f_lambertw(_sign * _mag, 1e-10, false));
            }
            if (_layer == 1)
            {
                return d_lambertw(this, 1e-10, false);
            }
            return neg().recip().lambertw().neg();
        }

        //from https://math.stackexchange.com/a/465183
        // The evaluation can become inaccurate very close to the branch point
        // Evaluates W(x, 0) if principal is true, W(x, -1) if principal is false
        private static double f_lambertw(double z, double tol = 1e-10, bool principal = true)
        {
            double w;
            double wn;

            if (!IsFiniteD(z))
            {
                return z;
            }
            if (principal)
            {
                if (z == 0)
                {
                    return z;
                }
                if (z == 1)
                {
                    return OMEGA;
                }

                if (z < 10)
                {
                    w = 0;
                }
                else
                {
                    w = Math.Log(z) - Math.Log(Math.Log(z));
                }
            }
            else
            {
                if (z == 0)
                {
                    return double.NegativeInfinity;
                }

                if (z <= -0.1)
                {
                    w = -2;
                }
                else
                {
                    w = Math.Log(-z) - Math.Log(-Math.Log(-z));
                }
            }

            for (int i = 0; i < 100; ++i)
            {
                wn = (z * Math.Exp(-w) + w * w) / (w + 1);
                if (Math.Abs(wn - w) < tol * Math.Abs(wn))
                {
                    return wn;
                }
                w = wn;
            }

            // break_eternity.js throws "Iteration failed to converge" here.
            return double.NaN;
        }

        //from https://github.com/scipy/scipy/blob/8dba340293fe20e62e173bdf2c10ae208286692f/scipy/special/lambertw.pxd
        // The evaluation can become inaccurate very close to the branch point
        // at ``-1/e``. In some corner cases, `lambertw` might currently
        // fail to converge, or can end up on the wrong branch.
        // Evaluates W(x, 0) if principal is true, W(x, -1) if principal is false
        private static BigDouble d_lambertw(BigDouble z, double tol = 1e-10, bool principal = true)
        {
            BigDouble w;
            BigDouble ew, wewz, wn;

            if (!IsFiniteD(z._mag))
            {
                return z;
            }
            if (principal)
            {
                if (z.eq(dZero))
                {
                    return dZero;
                }
                if (z.eq(dOne))
                {
                    //Split out this case because the asymptotic series blows up
                    return fromNumber(OMEGA);
                }

                //Get an initial guess for Halley's method
                w = BigDouble.ln(z);
            }
            else
            {
                if (z.eq(dZero))
                {
                    return dNegInf;
                }
                //Get an initial guess for Halley's method
                w = BigDouble.ln(z.neg());
            }
            //Halley's method; see 5.9 in [1]

            for (int i = 0; i < 100; ++i)
            {
                ew = w.neg().exp();
                wewz = w.sub(z.mul(ew));
                wn = w.sub(wewz.div(w.add(dOne).sub(w.add(dTwo).mul(wewz).div(BigDouble.mul(dTwo, w).add(dTwo)))));
                if (BigDouble.abs(wn.sub(w)).lt(BigDouble.abs(wn).mul(tol)))
                {
                    return wn;
                }
                w = wn;
            }

            // break_eternity.js throws "Iteration failed to converge" here.
            return dNaN;
        }

        #endregion

        #region Super-roots

        /// <summary>The super square root: which number, tetrated to height 2, equals this?</summary>
        public readonly BigDouble ssqrt()
        {
            return linear_sroot(2);
        }

        /// <summary>
        /// Super-root: which number, tetrated to height 'degree', equals this? Uses the linear approximation of
        /// tetration (only matters for non-integer degrees).
        /// </summary>
        public readonly BigDouble linear_sroot(double degree)
        {
            if (double.IsNaN(degree))
            {
                return dNaN;
            }
            //1st-degree super root just returns its input
            if (degree == 1)
            {
                return this;
            }
            if (eq(dInf))
            {
                return dInf;
            }
            if (!isFinite())
            {
                return dNaN;
            }
            //Using linear approximation, x^^n = x^n if 0 < n < 1
            if (degree > 0 && degree < 1)
            {
                return root(degree);
            }
            //Using the linear approximation, there actually is a single solution for super roots with -2 < degree <= -1
            if (degree > -2 && degree < -1)
            {
                return fromNumber(degree).add(dTwo).pow(recip());
            }
            //Super roots with -1 <= degree < 0 have either no solution or infinitely many solutions, and tetration with height <= -2 returns NaN, so super roots of degree <= -2 don't work
            if (degree <= 0)
            {
                return dNaN;
            }
            //Infinite degree super-root is x^(1/x) between 1/e <= x <= e, undefined otherwise
            if (double.IsPositiveInfinity(degree))
            {
                double this_num = toNumber();
                if (this_num < Math.E && this_num > EXPN1)
                {
                    return pow(recip());
                }
                return dNaN;
            }
            //Special case: any super-root of 1 is 1
            if (eq(dOne))
            {
                return dOne;
            }
            //TODO (from JS): base < 0 (It'll probably be NaN anyway)
            if (lt(dZero))
            {
                return dNaN;
            }
            //Treat all numbers of layer <= -2 as zero, because they effectively are
            if (lte(s_1eem16))
            {
                if (degree % 2 == 1)
                {
                    return this;
                }
                return dNaN;
            }
            //this > 1
            if (gt(dOne))
            {
                //Uses guess-and-check to find the super-root.
                //If this > 10^^(degree), then the answer is under iteratedlog(10, degree - 1): for example, ssqrt(x) < log(x, 10) as long as x > 10^10, and linear_sroot(x, 3) < log(log(x, 10), 10) as long as x > 10^10^10
                //On the other hand, if this < 10^^(degree), then clearly the answer is less than 10
                //Since the answer could be a higher-layered number itself (whereas slog's maximum is 1.8e308), the guess-and-check is scaled to the layer of the upper bound, so the guess is set to the average of some higher-layer exponents of the bounds rather than the bounds itself (as taking plain averages on tetrational-scale numbers would go nowhere)
                var upperBound = dTen;
                if (gte(BigDouble.tetrate(dTen, degree, dOne, true)))
                {
                    upperBound = iteratedlog(dTen, degree - 1, true);
                }
                if (degree <= 1)
                {
                    upperBound = root(degree);
                }
                var lower = dZero; //This is zero rather than one because we might be on a higher layer, so the lower bound might actually some 10^10^10...^0
                double layer = upperBound._layer;
                var upper = upperBound.iteratedlog(dTen, layer, true);
                var previous = upper;
                var guess = upper.div(dTwo);
                bool loopGoing = true;
                int steps = 0;
                while (loopGoing)
                {
                    guess = lower.add(upper).div(dTwo);
                    if (BigDouble.iteratedexp(dTen, layer, guess, true).tetrate(degree, dOne, true).gt(this))
                    {
                        upper = guess;
                    }
                    else
                    {
                        lower = guess;
                    }
                    if (guess.eq(previous))
                    {
                        loopGoing = false;
                    }
                    else
                    {
                        previous = guess;
                    }
                    if (++steps > MAX_BISECTION_STEPS)
                    {
                        return dNaN;
                    }
                }
                return BigDouble.iteratedexp(dTen, layer, guess, true);
            }
            //0 < this < 1
            return linear_sroot_small(degree);
        }

        // 0 < this < 1 branch of linear_sroot (separate method only to keep the main one readable).
        private readonly BigDouble linear_sroot_small(double degree)
        {
            //A tetration of decimal degree can potentially have three different portions, as shown at https://www.desmos.com/calculator/ayvqks6mxa, which is a graph of x^^2.05:
            //The portion where the function is increasing, extending from a minimum (which may be at x = 0 or at some x between 0 and 1) up to infinity (the "increasing" range)
            //The portion where the function is decreasing (the "decreasing" range)
            //A small, steep increasing portion very close to x = 0 (the "zero" range)
            //If ceiling(degree) is even, then the tetration will either be strictly increasing, or it will have the increasing and decreasing ranges, but not the zero range (because if ceiling(degree) is even, 0^^degree == 1).
            //If ceiling(degree) is odd, then the tetration will either be strictly increasing, or it will have all three ranges (because if ceiling(degree) is odd, 0^^degree == 0).
            //The existence of these ranges means that a super-root could potentially have two or three valid answers.
            //Out of these, we'd prefer the increasing range value if it exists, otherwise we'll take the zero range value (it can't have a decreasing range value if it doesn't have an increasing range value) if the zero range exists.
            //We need to find the local minimum (separates decreasing and increasing ranges) and the local maximum (separates zero and decreasing ranges).
            //(stage) is which stage of the loop we're in: stage 1 is finding the minimum, stage 2 means we're between the stages, and stage 3 is finding the maximum.
            //The boundary between the decreasing range and the zero range can be very small, so we want to use layer -1 numbers. Therefore, all numbers involved are log10(recip()) of their actual values.
            var effectivelyZero = FC(1, 10, 1); //eeeeeeeee-10, which is effectively 0; dInf cannot be used because its reciprocal is NaN
            int stage = 1;
            var minimum = effectivelyZero;
            var maximum = effectivelyZero;
            var lower = effectivelyZero;
            var upper = FC(1, 1, -16); //~ 1 - 1e-16
            var prevspan = dZero;
            var difference = effectivelyZero;
            var upperBound = upper.pow10().recip();
            var distance = dZero;
            var prevPoint = upperBound;
            var nextPoint = upperBound;
            bool evenDegree = Math.Ceiling(degree) % 2 == 0;
            int range = 0;
            var lastValid = effectivelyZero;
            bool infLoopDetector = false;
            var previousUpper = dZero;
            bool decreasingFound = false;
            int outerGuard = 0;
            while (stage < 4)
            {
                if (++outerGuard > MAX_BISECTION_STEPS)
                {
                    return dNaN;
                }
                if (stage == 2)
                {
                    //The minimum has been found. If ceiling(degree) is even, there's no zero range and thus no local maximum, so end the loop here. Otherwise, begin finding the maximum.
                    if (evenDegree)
                    {
                        break;
                    }
                    lower = effectivelyZero;
                    upper = minimum;
                    stage = 3;
                    difference = effectivelyZero;
                    lastValid = effectivelyZero;
                }
                infLoopDetector = false;
                int innerGuard = 0;
                while (upper.neq(lower))
                {
                    if (++innerGuard > MAX_BISECTION_STEPS)
                    {
                        return dNaN;
                    }
                    previousUpper = upper;
                    var upperPoint = upper.pow10().recip();
                    var upperTetr = upperPoint.tetrate(degree, dOne, true);
                    if (upperTetr.eq(dOne) && upperPoint.lt(0.4))
                    {
                        upperBound = upperPoint;
                        prevPoint = upperPoint;
                        nextPoint = upperPoint;
                        distance = dZero;
                        range = -1; //This would cause problems with degree < 1 in the linear approximation... but those are already covered as a special case
                        if (stage == 3)
                        {
                            lastValid = upper;
                        }
                    }
                    else if (upperTetr.eq(upperPoint) && !evenDegree && upperPoint.lt(0.4))
                    {
                        upperBound = upperPoint;
                        prevPoint = upperPoint;
                        nextPoint = upperPoint;
                        distance = dZero;
                        range = 0;
                    }
                    else if (upperTetr.eq(upperPoint.mul(dTwo).tetrate(degree, dOne, true)))
                    {
                        //If the upper bound is closer to zero than the next point with a discernable tetration, so surely it's in whichever range is closest to zero?
                        //This won't happen in a strictly increasing tetration, as there x^^degree ~= x as x approaches zero
                        upperBound = upperPoint;
                        prevPoint = dZero;
                        nextPoint = upperBound.mul(dTwo);
                        distance = upperBound;
                        range = evenDegree ? -1 : 0;
                    }
                    else
                    {
                        //We want to use prevspan to find the "previous point" right before the upper bound and the "next point" right after the upper bound, as that will let us approximate derivatives
                        prevspan = upper.mul(1.2e-16);
                        upperBound = upperPoint;
                        prevPoint = upper.add(prevspan).pow10().recip();
                        distance = upperBound.sub(prevPoint);
                        nextPoint = upperBound.add(distance);
                        //...but it's of no use to us while its tetration is equal to upper's tetration, so widen the difference until it's not
                        //We add prevspan and subtract nextspan because, since upper is log10(recip(upper bound)), the upper bound gets smaller as upper gets larger and vice versa
                        int widenGuard = 0;
                        while (prevPoint.tetrate(degree, dOne, true).eq(upperBound.tetrate(degree, dOne, true))
                               || nextPoint.tetrate(degree, dOne, true).eq(upperBound.tetrate(degree, dOne, true))
                               || prevPoint.gte(upperBound)
                               || nextPoint.lte(upperBound))
                        {
                            if (++widenGuard > MAX_BISECTION_STEPS)
                            {
                                return dNaN;
                            }
                            prevspan = prevspan.mul(dTwo);
                            prevPoint = upper.add(prevspan).pow10().recip();
                            distance = upperBound.sub(prevPoint);
                            nextPoint = upperBound.add(distance);
                        }
                        var boundTetr = upperBound.tetrate(degree, dOne, true);
                        if ((stage == 1 && nextPoint.tetrate(degree, dOne, true).gt(boundTetr) && prevPoint.tetrate(degree, dOne, true).gt(boundTetr))
                            || (stage == 3 && nextPoint.tetrate(degree, dOne, true).lt(boundTetr) && prevPoint.tetrate(degree, dOne, true).lt(boundTetr)))
                        {
                            lastValid = upper;
                        }
                        if (nextPoint.tetrate(degree, dOne, true).lt(boundTetr))
                        {
                            //Derivative is negative, so we're in decreasing range
                            range = -1;
                        }
                        else if (evenDegree)
                        {
                            //No zero range, so we're in increasing range
                            range = 1;
                        }
                        else if (stage == 3 && upper.gtTolerance(minimum, 1e-8))
                        {
                            //We're already below the minimum, so we can't be in range 1
                            range = 0;
                        }
                        else
                        {
                            //Number imprecision has left the second derivative somewhat untrustworthy, so we need to expand the bounds to ensure it's correct
                            widenGuard = 0;
                            while (prevPoint.tetrate(degree, dOne, true).eqTolerance(boundTetr, 1e-8)
                                   || nextPoint.tetrate(degree, dOne, true).eqTolerance(boundTetr, 1e-8)
                                   || prevPoint.gte(upperBound)
                                   || nextPoint.lte(upperBound))
                            {
                                if (++widenGuard > MAX_BISECTION_STEPS)
                                {
                                    return dNaN;
                                }
                                prevspan = prevspan.mul(dTwo);
                                prevPoint = upper.add(prevspan).pow10().recip();
                                distance = upperBound.sub(prevPoint);
                                nextPoint = upperBound.add(distance);
                            }
                            if (nextPoint.tetrate(degree, dOne, true).sub(boundTetr).lt(boundTetr.sub(prevPoint.tetrate(degree, dOne, true))))
                            {
                                //Second derivative is negative, so we're in zero range
                                range = 0;
                            }
                            else
                            {
                                //By process of elimination, we're in increasing range
                                range = 1;
                            }
                        }
                    }
                    if (range == -1)
                    {
                        decreasingFound = true;
                    }
                    if ((stage == 1 && range == 1) || (stage == 3 && range != 0))
                    {
                        //The upper bound is too high
                        if (lower.eq(effectivelyZero))
                        {
                            upper = upper.mul(dTwo);
                        }
                        else
                        {
                            bool cutOff = infLoopDetector && ((range == 1 && stage == 1) || (range == -1 && stage == 3)); //Avoids infinite loops from floating point imprecision
                            upper = upper.add(lower).div(dTwo);
                            if (cutOff)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        if (lower.eq(effectivelyZero))
                        {
                            //We've now found an actual lower bound
                            lower = upper;
                            upper = upper.div(dTwo);
                        }
                        else
                        {
                            //The upper bound is too low, meaning last time we decreased the upper bound, we should have gone to the other half of the new range instead
                            bool cutOff = infLoopDetector && ((range == 1 && stage == 1) || (range == -1 && stage == 3)); //Avoids infinite loops from floating point imprecision
                            lower = lower.sub(difference);
                            upper = upper.sub(difference);
                            if (cutOff)
                            {
                                break;
                            }
                        }
                    }
                    if (lower.sub(upper).div(dTwo).abs().gt(difference.mul(1.5)))
                    {
                        infLoopDetector = true;
                    }
                    difference = lower.sub(upper).div(dTwo).abs();
                    if (upper.gt(s_1e18))
                    {
                        break;
                    }
                    if (upper.eq(previousUpper))
                    {
                        break; //Another infinite loop catcher
                    }
                }
                if (upper.gt(s_1e18))
                {
                    break;
                }
                if (!decreasingFound)
                {
                    break; //If there's no decreasing range, then even if an error caused lastValid to gain a value, the minimum can't exist
                }
                if (lastValid.eq(effectivelyZero))
                {
                    //Whatever we're searching for, it doesn't exist. If there's no minimum, then there's no maximum either, so either way we can end the loop here.
                    break;
                }
                if (stage == 1)
                {
                    minimum = lastValid;
                }
                else if (stage == 3)
                {
                    maximum = lastValid;
                }
                stage++;
            }
            //Now we have the minimum and maximum, so it's time to calculate the actual super-root.
            //First, check if the root is in the increasing range.
            lower = minimum;
            upper = FC(1, 1, -18);
            var previous = upper;
            var guess = dZero;
            bool loopGoing = true;
            int steps = 0;
            while (loopGoing)
            {
                if (lower.eq(effectivelyZero))
                {
                    guess = upper.mul(dTwo);
                }
                else
                {
                    guess = lower.add(upper).div(dTwo);
                }
                if (BigDouble.pow(dTen, guess).recip().tetrate(degree, dOne, true).gt(this))
                {
                    upper = guess;
                }
                else
                {
                    lower = guess;
                }
                if (guess.eq(previous))
                {
                    loopGoing = false;
                }
                else
                {
                    previous = guess;
                }
                if (upper.gt(s_1e18) || ++steps > MAX_BISECTION_STEPS)
                {
                    return dNaN;
                }
            }
            //using guess.neq(minimum) led to imprecision errors, so here's a fixed version of that
            if (!guess.eqTolerance(minimum, 1e-15))
            {
                return guess.pow10().recip();
            }
            //If guess == minimum, we haven't actually found the super-root, the algorithm just kept going down trying to find a super-root that's not in the increasing range.
            //Check if the root is in the zero range.
            if (maximum.eq(effectivelyZero))
            {
                //There is no zero range, so the super root doesn't exist
                return dNaN;
            }
            lower = effectivelyZero;
            upper = maximum;
            previous = upper;
            guess = dZero;
            loopGoing = true;
            steps = 0;
            while (loopGoing)
            {
                if (lower.eq(effectivelyZero))
                {
                    guess = upper.mul(dTwo);
                }
                else
                {
                    guess = lower.add(upper).div(dTwo);
                }
                if (BigDouble.pow(dTen, guess).recip().tetrate(degree, dOne, true).gt(this))
                {
                    upper = guess;
                }
                else
                {
                    lower = guess;
                }
                if (guess.eq(previous))
                {
                    loopGoing = false;
                }
                else
                {
                    previous = guess;
                }
                if (upper.gt(s_1e18) || ++steps > MAX_BISECTION_STEPS)
                {
                    return dNaN;
                }
            }
            return guess.pow10().recip();
        }

        #endregion

        #region Inverse of increasing functions

        /// <summary>
        /// Takes a BigDouble =&gt; BigDouble function and returns a function that is its inverse, using binary search.
        /// Only intended for continuous, strictly increasing (or, with decreasing = true, strictly decreasing) functions.
        /// minX/maxX: domain of func (default ±<see cref="dLayerMax"/>). minY/maxY: range accepted by the inverse.
        /// </summary>
        public static Func<BigDouble, BigDouble> increasingInverse(
            Func<BigDouble, BigDouble> func,
            bool decreasing = false,
            int iterations = 120,
            BigDouble? minX = null,
            BigDouble? maxX = null,
            BigDouble? minY = null,
            BigDouble? maxY = null)
        {
            if (func == null)
            {
                throw new ArgumentNullException(nameof(func));
            }
            BigDouble minXv = minX ?? dLayerMax.neg();
            BigDouble maxXv = maxX ?? dLayerMax;
            BigDouble minYv = minY ?? dLayerMax.neg();
            BigDouble maxYv = maxY ?? dLayerMax;

            return value =>
            {
                if (value.isNan() || maxXv.lt(minXv) || value.lt(minYv) || value.gt(maxYv))
                {
                    return dNaN;
                }

                // Before actually doing the search, let's determine what range we're looking at.
                Func<BigDouble, BigDouble> rangeApply = v => v;
                bool currentCheck; // Checking whether the inverse is positive
                if (maxXv.lt(dZero))
                {
                    currentCheck = false;
                }
                else if (minXv.gt(dZero))
                {
                    currentCheck = true;
                }
                else
                {
                    var valCheck = func(dZero);
                    if (valCheck.eq(value))
                    {
                        return dZero;
                    }
                    currentCheck = value.gt(valCheck);
                    if (decreasing)
                    {
                        currentCheck = !currentCheck;
                    }
                }
                bool positive = currentCheck;
                bool reciprocal;
                if (currentCheck)
                {
                    // Checking whether the inverse is below 1/9e15
                    if (maxXv.lt(FIRST_NEG_LAYER))
                    {
                        currentCheck = true;
                    }
                    else if (minXv.gt(FIRST_NEG_LAYER))
                    {
                        currentCheck = false;
                    }
                    else
                    {
                        var valCheck = func(fromNumber(FIRST_NEG_LAYER));
                        currentCheck = value.lt(valCheck);
                        if (decreasing)
                        {
                            currentCheck = !currentCheck;
                        }
                    }
                    if (currentCheck)
                    {
                        reciprocal = true;
                        // Checking whether the inverse is above 1/e9e15
                        var limit = BigDouble.pow(dTen, EXP_LIMIT).recip();
                        if (maxXv.lt(limit))
                        {
                            currentCheck = false;
                        }
                        else if (minXv.gt(limit))
                        {
                            currentCheck = true;
                        }
                        else
                        {
                            var valCheck = func(limit);
                            currentCheck = value.gt(valCheck);
                            if (decreasing)
                            {
                                currentCheck = !currentCheck;
                            }
                        }
                        // If the inverse is between 1/e9e15 and 1/9e15, search through layer -1 numbers.
                        if (currentCheck)
                        {
                            rangeApply = v => BigDouble.pow(dTen, v).recip();
                        }
                        else
                        {
                            // Checking whether the inverse is above 1/10^^9e15
                            var limit2 = BigDouble.tetrate(dTen, EXP_LIMIT);
                            if (maxXv.lt(limit2))
                            {
                                currentCheck = false;
                            }
                            else if (minXv.gt(limit2))
                            {
                                currentCheck = true;
                            }
                            else
                            {
                                var valCheck = func(limit2);
                                currentCheck = value.gt(valCheck);
                                if (decreasing)
                                {
                                    currentCheck = !currentCheck;
                                }
                            }
                            // If the inverse is between 1/10^^9e15 and 1/e9e15, search through reciprocals of negative layers themselves.
                            if (currentCheck)
                            {
                                rangeApply = v => BigDouble.tetrate(dTen, v.toNumber()).recip();
                            }
                            // If the inverse is below 1/10^^9e15, search through reciprocals of exponents of layers.
                            else
                            {
                                rangeApply = v => v.gt(Math.Log10(double.MaxValue)) ? dZero : BigDouble.tetrate(dTen, BigDouble.pow(dTen, v).toNumber()).recip();
                            }
                        }
                    }
                    else
                    {
                        reciprocal = false;
                        // Checking whether the inverse is below 9e15
                        if (maxXv.lt(EXP_LIMIT))
                        {
                            currentCheck = true;
                        }
                        else if (minXv.gt(EXP_LIMIT))
                        {
                            currentCheck = false;
                        }
                        else
                        {
                            var valCheck = func(fromNumber(EXP_LIMIT));
                            currentCheck = value.lt(valCheck);
                            if (decreasing)
                            {
                                currentCheck = !currentCheck;
                            }
                        }
                        // If the inverse is between 1/9e15 and 9e15, search through direct (layer 0) numbers.
                        if (currentCheck)
                        {
                            rangeApply = v => v;
                        }
                        else
                        {
                            // Checking whether the inverse is below e9e15
                            var limit = BigDouble.pow(dTen, EXP_LIMIT);
                            if (maxXv.lt(limit))
                            {
                                currentCheck = true;
                            }
                            else if (minXv.gt(limit))
                            {
                                currentCheck = false;
                            }
                            else
                            {
                                var valCheck = func(limit);
                                currentCheck = value.lt(valCheck);
                                if (decreasing)
                                {
                                    currentCheck = !currentCheck;
                                }
                            }
                            // If the inverse is between 9e15 and e9e15, search through layer 1 numbers.
                            if (currentCheck)
                            {
                                rangeApply = v => BigDouble.pow(dTen, v);
                            }
                            else
                            {
                                // Checking whether the inverse is below 10^^9e15
                                var limit2 = BigDouble.tetrate(dTen, EXP_LIMIT);
                                if (maxXv.lt(limit2))
                                {
                                    currentCheck = true;
                                }
                                else if (minXv.gt(limit2))
                                {
                                    currentCheck = false;
                                }
                                else
                                {
                                    var valCheck = func(limit2);
                                    currentCheck = value.lt(valCheck);
                                    if (decreasing)
                                    {
                                        currentCheck = !currentCheck;
                                    }
                                }
                                // If the inverse is between e9e15 and 10^^9e15, search through layers themselves.
                                if (currentCheck)
                                {
                                    rangeApply = v => BigDouble.tetrate(dTen, v.toNumber());
                                }
                                // If the inverse is above 10^^9e15, search through exponents of layers.
                                else
                                {
                                    rangeApply = v => v.gt(Math.Log10(double.MaxValue)) ? dInf : BigDouble.tetrate(dTen, BigDouble.pow(dTen, v).toNumber());
                                }
                            }
                        }
                    }
                }
                else
                {
                    reciprocal = true;
                    // Checking whether the inverse is above -1/9e15
                    if (maxXv.lt(-FIRST_NEG_LAYER))
                    {
                        currentCheck = false;
                    }
                    else if (minXv.gt(-FIRST_NEG_LAYER))
                    {
                        currentCheck = true;
                    }
                    else
                    {
                        var valCheck = func(fromNumber(-FIRST_NEG_LAYER));
                        currentCheck = value.gt(valCheck);
                        if (decreasing)
                        {
                            currentCheck = !currentCheck;
                        }
                    }
                    if (currentCheck)
                    {
                        // Checking whether the inverse is below -1/e9e15
                        var limit = BigDouble.pow(dTen, EXP_LIMIT).recip().neg();
                        if (maxXv.lt(limit))
                        {
                            currentCheck = true;
                        }
                        else if (minXv.gt(limit))
                        {
                            currentCheck = false;
                        }
                        else
                        {
                            var valCheck = func(limit);
                            currentCheck = value.lt(valCheck);
                            if (decreasing)
                            {
                                currentCheck = !currentCheck;
                            }
                        }
                        // If the inverse is between -1/e9e15 and -1/9e15, search through negatives of layer -1 numbers.
                        if (currentCheck)
                        {
                            rangeApply = v => BigDouble.pow(dTen, v).recip().neg();
                        }
                        else
                        {
                            // Checking whether the inverse is below -1/10^^9e15
                            var limit2 = BigDouble.tetrate(dTen, EXP_LIMIT).neg();
                            if (maxXv.lt(limit2))
                            {
                                currentCheck = true;
                            }
                            else if (minXv.gt(limit2))
                            {
                                currentCheck = false;
                            }
                            else
                            {
                                var valCheck = func(limit2);
                                currentCheck = value.lt(valCheck);
                                if (decreasing)
                                {
                                    currentCheck = !currentCheck;
                                }
                            }
                            // If the inverse is between -1/10^^9e15 and -1/e9e15, search through reciprocals of negative layers themselves.
                            if (currentCheck)
                            {
                                rangeApply = v => BigDouble.tetrate(dTen, v.toNumber()).recip().neg();
                            }
                            // If the inverse is above -1/10^^9e15, search through exponents of reciprocals of negative layers.
                            else
                            {
                                rangeApply = v => v.gt(Math.Log10(double.MaxValue)) ? dZero : BigDouble.tetrate(dTen, BigDouble.pow(dTen, v).toNumber()).recip().neg();
                            }
                        }
                    }
                    else
                    {
                        reciprocal = false;
                        // Checking whether the inverse is above -9e15
                        if (maxXv.lt(-EXP_LIMIT))
                        {
                            currentCheck = false;
                        }
                        else if (minXv.gt(-EXP_LIMIT))
                        {
                            currentCheck = true;
                        }
                        else
                        {
                            var valCheck = func(fromNumber(-EXP_LIMIT));
                            currentCheck = value.gt(valCheck);
                            if (decreasing)
                            {
                                currentCheck = !currentCheck;
                            }
                        }
                        // If the inverse is between -1/9e15 and -9e15, search through negatives of direct (layer 0) numbers.
                        if (currentCheck)
                        {
                            rangeApply = v => v.neg();
                        }
                        else
                        {
                            // Checking whether the inverse is above -e9e15
                            var limit = BigDouble.pow(dTen, EXP_LIMIT).neg();
                            if (maxXv.lt(limit))
                            {
                                currentCheck = false;
                            }
                            else if (minXv.gt(limit))
                            {
                                currentCheck = true;
                            }
                            else
                            {
                                var valCheck = func(limit);
                                currentCheck = value.gt(valCheck);
                                if (decreasing)
                                {
                                    currentCheck = !currentCheck;
                                }
                            }
                            // If the inverse is between -9e15 and -e9e15, search through negatives of layer 1 numbers.
                            if (currentCheck)
                            {
                                rangeApply = v => BigDouble.pow(dTen, v).neg();
                            }
                            else
                            {
                                // Checking whether the inverse is above -10^^9e15
                                var limit2 = BigDouble.tetrate(dTen, EXP_LIMIT).neg();
                                if (maxXv.lt(limit2))
                                {
                                    currentCheck = false;
                                }
                                else if (minXv.gt(limit2))
                                {
                                    currentCheck = true;
                                }
                                else
                                {
                                    var valCheck = func(limit2);
                                    currentCheck = value.gt(valCheck);
                                    if (decreasing)
                                    {
                                        currentCheck = !currentCheck;
                                    }
                                }
                                // If the inverse is between e9e15 and 10^^9e15, search through negatives of layers themselves.
                                if (currentCheck)
                                {
                                    rangeApply = v => BigDouble.tetrate(dTen, v.toNumber()).neg();
                                }
                                // If the inverse is below -10^^9e15, search through negatives of exponents of layers.
                                else
                                {
                                    rangeApply = v => v.gt(Math.Log10(double.MaxValue)) ? dNegInf : BigDouble.tetrate(dTen, BigDouble.pow(dTen, v).toNumber()).neg();
                                }
                            }
                        }
                    }
                }
                bool searchIncreasing = (positive != reciprocal) != decreasing;
                Func<BigDouble, BigDouble, bool> comparative;
                if (searchIncreasing)
                {
                    comparative = (a, b) => a.gt(b);
                }
                else
                {
                    comparative = (a, b) => a.lt(b);
                }

                double step_size = 0.001;
                bool has_changed_directions_once = false;
                bool previously_rose = false;
                double result = 1;
                BigDouble appliedResult;
                double oldresult;
                bool critical;
                for (int i = 1; i < iterations; ++i)
                {
                    critical = false;
                    oldresult = result;
                    appliedResult = rangeApply(fromNumber(result));
                    //Under no circumstances should we be calling func on something outside its domain.
                    if (appliedResult.gt(maxXv))
                    {
                        appliedResult = maxXv;
                        critical = true;
                    }
                    if (appliedResult.lt(minXv))
                    {
                        appliedResult = minXv;
                        critical = true;
                    }
                    var new_decimal = func(appliedResult);
                    if (new_decimal.eq(value) && !critical)
                    {
                        break;
                    }
                    bool currently_rose = comparative(new_decimal, value);
                    if (i > 1)
                    {
                        if (previously_rose != currently_rose)
                        {
                            has_changed_directions_once = true;
                        }
                    }
                    previously_rose = currently_rose;
                    if (has_changed_directions_once)
                    {
                        step_size /= 2;
                    }
                    else
                    {
                        step_size *= 2;
                    }
                    // If the inverse is trying to increase when it's already at maxX, or it's trying to decrease when it's already at minX, it's going outside the domain, so return NaN.
                    if ((currently_rose != searchIncreasing && appliedResult.eq(maxXv)) || (currently_rose == searchIncreasing && appliedResult.eq(minXv)))
                    {
                        return dNaN;
                    }
                    step_size = Math.Abs(step_size) * (currently_rose ? -1 : 1);
                    result += step_size;
                    if (step_size == 0 || oldresult == result)
                    {
                        break;
                    }
                }
                return rangeApply(fromNumber(result));
            };
        }

        #endregion

        #region Pentation

        /// <summary>
        /// Pentation: this ^^^ height (tetrating height times in a row). Non-whole heights always use the linear
        /// approximation. 2 ^^^ 4.28 and 10 ^^^ 2.37 already overflow.
        /// </summary>
        public readonly BigDouble pentate(double height = 2, BigDouble? payload = null, bool linear = false)
        {
            BigDouble p = payload ?? dOne;
            double oldheight = height;
            height = Math.Floor(height);
            double fracheight = oldheight - height;
            var prevpayload = dZero;
            var prevtwopayload = dZero;

            // Linear approximation. No idea if this is a meaningful approximation for pentation to continuous heights, but it is monotonic and continuous.
            if (fracheight != 0)
            {
                if (p.eq(dOne))
                {
                    ++height;
                    p = fromNumber(fracheight);
                }
                else
                {
                    // Despite calling penta_log, this is safe, as penta_log only calls pentation with payload 1.
                    return pentate(p.penta_log(this, 100, linear).plus(oldheight).toNumber(), dOne, linear);
                }
            }

            if (height > 0)
            {
                for (int i = 0; i < height;)
                {
                    prevtwopayload = prevpayload;
                    prevpayload = p;
                    p = tetrate(p.toNumber(), dOne, linear);
                    ++i;
                    // Under the linear approximation of pentation, if p is between 0 and 1, x^^^p == x^^p (which just equals x^p if tetration is also using the linear approximation)
                    // ...and once both the base and payload are between 0 and 1, they'll both stay that way.
                    if (gt(dZero) && lte(dOne) && p.gt(dZero) && p.lte(dOne))
                    {
                        return tetrate(height - i, p, linear);
                    }
                    // End early if it's settled on a limit. Bases close to 0 alternate between two possible values.
                    if (p.eq(prevpayload) || (p.eq(prevtwopayload) && (i % 2 == height % 2)))
                    {
                        return p.normalize();
                    }
                    //bail if we're NaN
                    if (!IsFiniteD(p._layer) || !IsFiniteD(p._mag))
                    {
                        return p.normalize();
                    }
                    //give up after 10000 iterations if nothing is happening
                    if (i > 10000)
                    {
                        return p;
                    }
                }
            }
            else
            {
                // Repeated slog is sloooow, but that's what negative pentation height means.
                for (int i = 0; i < -height; ++i)
                {
                    prevpayload = p;
                    p = p.slog(this, 100, linear);
                    // End early if it's settled on a limit
                    if (p.eq(prevpayload))
                    {
                        return p.normalize();
                    }
                    //bail if we're NaN
                    if (!IsFiniteD(p._layer) || !IsFiniteD(p._mag))
                    {
                        return p.normalize();
                    }
                    //give up after 100 iterations if nothing is happening
                    if (i > 100)
                    {
                        return p;
                    }
                }
            }

            return p;
        }

        /// <summary>
        /// Penta-logarithm: what height you'd have to pentate 'base' to to get this. Grows incredibly slowly.
        /// Very slow for values &lt;= -1.
        /// </summary>
        public readonly BigDouble penta_log(BigDouble? @base = null, int iterations = 100, bool linear = false)
        {
            BigDouble b = @base ?? dTen;
            if (isNan())
            {
                return dNaN;
            }
            // Bases below 1 oscillate, so the logarithm doesn't make sense
            if (b.lte(dOne))
            {
                return dNaN;
            }
            if (eq(dOne))
            {
                return dZero;
            }
            if (eq(dInf))
            {
                return dInf;
            }
            var value = dOne;
            double result = 0;
            double step_size = 1;

            // There's some x between -1 and -2, depending on the base, where base^^x == x. This x is base^^^(-Infinity), and we shouldn't bother with numbers less than that limit.
            if (lt(dNegOne))
            {
                if (lte(-2))
                {
                    return dNaN;
                }
                var limitcheck = b.tetrate(toNumber(), dOne, linear);
                if (eq(limitcheck))
                {
                    return dNegInf;
                }
                if (gt(limitcheck))
                {
                    return dNaN;
                }
            }

            // pentate runs through each tetration iteration anyway, so while we're narrowing down on the nearest integer it's faster to just check them one-by-one than to run through pentate every time
            if (gt(dOne))
            {
                while (value.lt(this))
                {
                    result++;
                    value = BigDouble.tetrate(b, value.toNumber(), dOne, linear);
                    if (result > 1000)
                    {
                        // Probably reached a limit by this point.
                        return dNaN;
                    }
                }
            }
            else
            {
                while (value.gt(this))
                {
                    result--;
                    value = BigDouble.slog(value, b, linear);
                    // break_eternity.js checks 'result > 100' here, which never triggers because result decreases.
                    if (result < -100)
                    {
                        // Probably reached the limit by this point.
                        return dNaN;
                    }
                }
            }
            for (int i = 1; i < iterations; ++i)
            {
                var new_decimal = b.pentate(result, dOne, linear);
                if (new_decimal.eq(this))
                {
                    break;
                }
                bool currently_rose = new_decimal.gt(this);
                step_size = Math.Abs(step_size) * (currently_rose ? -1 : 1);
                result += step_size;
                step_size /= 2;
                if (step_size == 0)
                {
                    break;
                }
            }
            return fromNumber(result);
        }

        /// <summary>
        /// Penta-root: which number, pentated to height 'degree', equals this? Uses the linear approximation of tetration.
        /// </summary>
        public readonly BigDouble linear_penta_root(double degree)
        {
            //1st-degree super root just returns its input
            if (degree == 1)
            {
                return this;
            }
            //TODO (from JS): degree < 0 (A pretty useless case)
            if (degree < 0 || double.IsNaN(degree))
            {
                return dNaN;
            }
            if (eq(dInf))
            {
                return dInf;
            }
            if (!isFinite())
            {
                return dNaN;
            }
            //Using linear approximation, x^^^n = x^n if 0 < n < 1
            if (degree > 0 && degree < 1)
            {
                return root(degree);
            }
            //Special case: any super-root of 1 is 1
            if (eq(dOne))
            {
                return dOne;
            }
            //TODO (from JS): base < 0 (It'll probably be NaN anyway)
            if (lt(dZero))
            {
                return dNaN;
            }
            /* If 'this' is below 1, then the result is going to be below 1, meaning the original number was a tetration tower where all entries are below 1...
            ...and in the linear approximation, tetration with a height between 0 and 1 reduces to exponentiation of that height.
            Therefore, penta_root and sroot will return the same value. */
            if (lt(dOne))
            {
                return linear_sroot(degree);
            }

            // Unlike with super-root, there is no good upper bound here, so let's just call increasingInverse.
            return increasingInverse(v => BigDouble.pentate(v, degree, dOne, true))(this);
        }

        #endregion

        // ============================================================================================================
        // 5. Lowercase static functions with the names of break_eternity.js (Decimal.add(a, b) ...) and of the previous
        // C# port. They exist for compatibility; new code can use the operators and the PascalCase API of section 2.
        //
        // ============================================================================================================

        #region Construction

        /// <summary>Creates a copy of the value.</summary>
        public static BigDouble fromValue(BigDouble value)
        {
            return value;
        }

        /// <summary>Converts a double.</summary>
        public static BigDouble fromValue(double value)
        {
            return fromNumber(value);
        }

        /// <summary>Parses a string (throws <see cref="FormatException"/> on invalid input).</summary>
        public static BigDouble fromValue(string value)
        {
            return Parse(value);
        }

        /// <summary>Same as <see cref="fromNumber"/> (name of the previous C# port).</summary>
        public static BigDouble fromDouble(double value)
        {
            return fromNumber(value);
        }

        /// <summary>Creates a copy of the value (name of the previous C# port).</summary>
        public static BigDouble fromBigDouble(BigDouble value)
        {
            return value;
        }

        /// <summary>Same as <see cref="fromComponents_noNormalize"/> (name of the previous C# port).</summary>
        public static BigDouble fromComponentsNoNormalize(double sign, double layer, double mag)
        {
            return FC_NN(sign, layer, mag);
        }

        /// <summary>Same as <see cref="fromMantissaExponent"/> (name of the previous C# port).</summary>
        public static BigDouble fromMantissaExponentNoNormalize(double mantissa, double exponent)
        {
            return fromMantissaExponent(mantissa, exponent);
        }

        /// <summary>Random value for tests (name of the previous C# port).</summary>
        public static BigDouble randomBigDoubleForTesting(double maxLayers)
        {
            return randomDecimalForTesting(maxLayers);
        }

        /// <summary>10^power for integer powers in [-323, 308] from a lookup table.</summary>
        public static double powerOf10(int power)
        {
            return PowersOf10.Get(power);
        }

        #endregion

        #region Classification

        /// <summary>True if value is NaN.</summary>
        public static bool isNaN(BigDouble value)
        {
            return value.isNan();
        }

        /// <summary>True if value is finite.</summary>
        public static bool isFinite(BigDouble value)
        {
            return value.isFinite();
        }

        /// <summary>True if value is ±Infinity.</summary>
        public static bool isInfinity(BigDouble value)
        {
            return IsInfinity(value);
        }

        /// <summary>True if value is +Infinity.</summary>
        public static bool isPositiveInfinity(BigDouble value)
        {
            return IsPositiveInfinity(value);
        }

        /// <summary>True if value is -Infinity.</summary>
        public static bool isNegativeInfinity(BigDouble value)
        {
            return IsNegativeInfinity(value);
        }

        #endregion

        #region Sign and rounding

        /// <summary>Absolute value.</summary>
        public static BigDouble abs(BigDouble value)
        {
            return value.abs();
        }

        /// <summary>Negation.</summary>
        public static BigDouble neg(BigDouble value)
        {
            return value.neg();
        }

        /// <summary>Negation.</summary>
        public static BigDouble negate(BigDouble value)
        {
            return value.neg();
        }

        /// <summary>Negation.</summary>
        public static BigDouble negated(BigDouble value)
        {
            return value.neg();
        }

        /// <summary>Sign of the value (-1, 0, 1 or NaN).</summary>
        public static double sgn(BigDouble value)
        {
            return value._sign;
        }

        /// <summary>Sign of the value as a BigDouble (-1, 0, 1 or NaN).</summary>
        public static BigDouble signum(BigDouble value)
        {
            return fromNumber(value._sign);
        }

        /// <summary>Rounds to the nearest integer (halves away from zero).</summary>
        public static BigDouble round(BigDouble value)
        {
            return value.round();
        }

        /// <summary>Rounds down.</summary>
        public static BigDouble floor(BigDouble value)
        {
            return value.floor();
        }

        /// <summary>Rounds up.</summary>
        public static BigDouble ceil(BigDouble value)
        {
            return value.ceil();
        }

        /// <summary>Rounds towards zero.</summary>
        public static BigDouble trunc(BigDouble value)
        {
            return value.trunc();
        }

        #endregion

        #region Arithmetic

        /// <summary>value + other</summary>
        public static BigDouble add(BigDouble value, BigDouble other)
        {
            return value.add(other);
        }

        /// <summary>value + other</summary>
        public static BigDouble plus(BigDouble value, BigDouble other)
        {
            return value.add(other);
        }

        /// <summary>value - other</summary>
        public static BigDouble sub(BigDouble value, BigDouble other)
        {
            return value.sub(other);
        }

        /// <summary>value - other</summary>
        public static BigDouble subtract(BigDouble value, BigDouble other)
        {
            return value.sub(other);
        }

        /// <summary>value - other</summary>
        public static BigDouble minus(BigDouble value, BigDouble other)
        {
            return value.sub(other);
        }

        /// <summary>value * other</summary>
        public static BigDouble mul(BigDouble value, BigDouble other)
        {
            return value.mul(other);
        }

        /// <summary>value * other</summary>
        public static BigDouble multiply(BigDouble value, BigDouble other)
        {
            return value.mul(other);
        }

        /// <summary>value * other</summary>
        public static BigDouble times(BigDouble value, BigDouble other)
        {
            return value.mul(other);
        }

        /// <summary>value / other</summary>
        public static BigDouble div(BigDouble value, BigDouble other)
        {
            return value.div(other);
        }

        /// <summary>value / other</summary>
        public static BigDouble divide(BigDouble value, BigDouble other)
        {
            return value.div(other);
        }

        /// <summary>1 / value</summary>
        public static BigDouble recip(BigDouble value)
        {
            return value.recip();
        }

        /// <summary>1 / value</summary>
        public static BigDouble reciprocal(BigDouble value)
        {
            return value.recip();
        }

        /// <summary>1 / value</summary>
        public static BigDouble reciprocate(BigDouble value)
        {
            return value.recip();
        }

        /// <summary>Remainder (see <see cref="mod(BigDouble, bool)"/>).</summary>
        public static BigDouble mod(BigDouble value, BigDouble other, bool floored = false)
        {
            return value.mod(other, floored);
        }

        /// <summary>Remainder (see <see cref="mod(BigDouble, bool)"/>).</summary>
        public static BigDouble modulo(BigDouble value, BigDouble other, bool floored = false)
        {
            return value.mod(other, floored);
        }

        /// <summary>Remainder (see <see cref="mod(BigDouble, bool)"/>).</summary>
        public static BigDouble modular(BigDouble value, BigDouble other, bool floored = false)
        {
            return value.mod(other, floored);
        }

        #endregion

        #region Comparison

        /// <summary>Total-order comparison (-1, 0, 1).</summary>
        public static int cmp(BigDouble value, BigDouble other)
        {
            return value.cmp(other);
        }

        /// <summary>Compares absolute values.</summary>
        public static int cmpabs(BigDouble value, BigDouble other)
        {
            return value.cmpabs(other);
        }

        /// <summary>Total-order comparison (-1, 0, 1).</summary>
        public static int compare(BigDouble value, BigDouble other)
        {
            return value.cmp(other);
        }

        /// <summary>value == other</summary>
        public static bool eq(BigDouble value, BigDouble other)
        {
            return value.eq(other);
        }

        /// <summary>value == other</summary>
        public static bool equals(BigDouble value, BigDouble other)
        {
            return value.eq(other);
        }

        /// <summary>value != other</summary>
        public static bool neq(BigDouble value, BigDouble other)
        {
            return value.neq(other);
        }

        /// <summary>value != other</summary>
        public static bool notEquals(BigDouble value, BigDouble other)
        {
            return value.neq(other);
        }

        /// <summary>value &lt; other</summary>
        public static bool lt(BigDouble value, BigDouble other)
        {
            return value.lt(other);
        }

        /// <summary>value &lt;= other</summary>
        public static bool lte(BigDouble value, BigDouble other)
        {
            return value.lte(other);
        }

        /// <summary>value &gt; other</summary>
        public static bool gt(BigDouble value, BigDouble other)
        {
            return value.gt(other);
        }

        /// <summary>value &gt;= other</summary>
        public static bool gte(BigDouble value, BigDouble other)
        {
            return value.gte(other);
        }

        /// <summary>The larger value.</summary>
        public static BigDouble max(BigDouble value, BigDouble other)
        {
            return value.max(other);
        }

        /// <summary>The smaller value.</summary>
        public static BigDouble min(BigDouble value, BigDouble other)
        {
            return value.min(other);
        }

        /// <summary>The value with the smaller absolute value.</summary>
        public static BigDouble minabs(BigDouble value, BigDouble other)
        {
            return value.minabs(other);
        }

        /// <summary>The value with the larger absolute value.</summary>
        public static BigDouble maxabs(BigDouble value, BigDouble other)
        {
            return value.maxabs(other);
        }

        /// <summary>Restricts value to [min, max].</summary>
        public static BigDouble clamp(BigDouble value, BigDouble min, BigDouble max)
        {
            return value.clamp(min, max);
        }

        /// <summary>Returns min if value is smaller than min.</summary>
        public static BigDouble clampMin(BigDouble value, BigDouble min)
        {
            return value.clampMin(min);
        }

        /// <summary>Returns max if value is greater than max.</summary>
        public static BigDouble clampMax(BigDouble value, BigDouble max)
        {
            return value.clampMax(max);
        }

        /// <summary>Comparison where approximately equal values compare as 0.</summary>
        public static int cmpTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.cmpTolerance(other, tolerance);
        }

        /// <summary>Comparison where approximately equal values compare as 0.</summary>
        public static int compareTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.cmpTolerance(other, tolerance);
        }

        /// <summary>Approximate equality.</summary>
        public static bool eqTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.eqTolerance(other, tolerance);
        }

        /// <summary>Approximate equality.</summary>
        public static bool equalsTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.eqTolerance(other, tolerance);
        }

        /// <summary>Approximate inequality.</summary>
        public static bool neqTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.neqTolerance(other, tolerance);
        }

        /// <summary>Approximate inequality.</summary>
        public static bool notEqualsTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.neqTolerance(other, tolerance);
        }

        /// <summary>value &lt; other and not approximately equal.</summary>
        public static bool ltTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.ltTolerance(other, tolerance);
        }

        /// <summary>value &lt; other or approximately equal.</summary>
        public static bool lteTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.lteTolerance(other, tolerance);
        }

        /// <summary>value &gt; other and not approximately equal.</summary>
        public static bool gtTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.gtTolerance(other, tolerance);
        }

        /// <summary>value &gt; other or approximately equal.</summary>
        public static bool gteTolerance(BigDouble value, BigDouble other, double tolerance = 1e-7)
        {
            return value.gteTolerance(other, tolerance);
        }

        #endregion

        #region Logarithms, powers, roots

        /// <summary>log10 for positive values, 0 otherwise.</summary>
        public static BigDouble pLog10(BigDouble value)
        {
            return value.pLog10();
        }

        /// <summary>log10(|value|)</summary>
        public static BigDouble absLog10(BigDouble value)
        {
            return value.absLog10();
        }

        /// <summary>Base-10 logarithm.</summary>
        public static BigDouble log10(BigDouble value)
        {
            return value.log10();
        }

        /// <summary>Logarithm to an arbitrary base.</summary>
        public static BigDouble log(BigDouble value, BigDouble @base)
        {
            return value.log(@base);
        }

        /// <summary>Base-2 logarithm.</summary>
        public static BigDouble log2(BigDouble value)
        {
            return value.log2();
        }

        /// <summary>Natural logarithm.</summary>
        public static BigDouble ln(BigDouble value)
        {
            return value.ln();
        }

        /// <summary>Logarithm to an arbitrary base.</summary>
        public static BigDouble logarithm(BigDouble value, BigDouble @base)
        {
            return value.log(@base);
        }

        /// <summary>value ^ other</summary>
        public static BigDouble pow(BigDouble value, BigDouble other)
        {
            return value.pow(other);
        }

        /// <summary>10 ^ value</summary>
        public static BigDouble pow10(BigDouble value)
        {
            return value.pow10();
        }

        /// <summary>The other-th root of value.</summary>
        public static BigDouble root(BigDouble value, BigDouble other)
        {
            return value.root(other);
        }

        /// <summary>value!</summary>
        public static BigDouble factorial(BigDouble value)
        {
            return value.factorial();
        }

        /// <summary>Gamma function.</summary>
        public static BigDouble gamma(BigDouble value)
        {
            return value.gamma();
        }

        /// <summary>ln(gamma(value))</summary>
        public static BigDouble lngamma(BigDouble value)
        {
            return value.lngamma();
        }

        /// <summary>e ^ value</summary>
        public static BigDouble exp(BigDouble value)
        {
            return value.exp();
        }

        /// <summary>value²</summary>
        public static BigDouble sqr(BigDouble value)
        {
            return value.sqr();
        }

        /// <summary>Square root.</summary>
        public static BigDouble sqrt(BigDouble value)
        {
            return value.sqrt();
        }

        /// <summary>value³</summary>
        public static BigDouble cube(BigDouble value)
        {
            return value.cube();
        }

        /// <summary>Cube root.</summary>
        public static BigDouble cbrt(BigDouble value)
        {
            return value.cbrt();
        }

        #endregion

        #region Hyperoperators

        /// <summary>Tetration (see instance method).</summary>
        public static BigDouble tetrate(BigDouble value, double height = 2, BigDouble? payload = null, bool linear = false)
        {
            return value.tetrate(height, payload, linear);
        }

        /// <summary>Iterated exponentiation (see instance method).</summary>
        public static BigDouble iteratedexp(BigDouble value, double height = 2, BigDouble? payload = null, bool linear = false)
        {
            return value.tetrate(height, payload, linear);
        }

        /// <summary>Iterated logarithm (see instance method).</summary>
        public static BigDouble iteratedlog(BigDouble value, BigDouble? @base = null, double times = 1, bool linear = false)
        {
            return value.iteratedlog(@base, times, linear);
        }

        /// <summary>Adds layers (see instance method).</summary>
        public static BigDouble layeradd10(BigDouble value, BigDouble diff, bool linear = false)
        {
            return value.layeradd10(diff, linear);
        }

        /// <summary>Adds to the slog representation (see instance method).</summary>
        public static BigDouble layeradd(BigDouble value, double diff, BigDouble? @base = null, bool linear = false)
        {
            return value.layeradd(diff, @base, linear);
        }

        /// <summary>Super-logarithm (see instance method).</summary>
        public static BigDouble slog(BigDouble value, BigDouble? @base = null, bool linear = false)
        {
            return value.slog(@base, 100, linear);
        }

        /// <summary>Lambert W function (see instance method).</summary>
        public static BigDouble lambertw(BigDouble value, bool principal = true)
        {
            return value.lambertw(principal);
        }

        /// <summary>Super square root (see instance method).</summary>
        public static BigDouble ssqrt(BigDouble value)
        {
            return value.ssqrt();
        }

        /// <summary>Super-root (see instance method).</summary>
        public static BigDouble linear_sroot(BigDouble value, double degree)
        {
            return value.linear_sroot(degree);
        }

        /// <summary>Pentation (see instance method).</summary>
        public static BigDouble pentate(BigDouble value, double height = 2, BigDouble? payload = null, bool linear = false)
        {
            return value.pentate(height, payload, linear);
        }

        /// <summary>Penta-logarithm (see instance method).</summary>
        public static BigDouble penta_log(BigDouble value, BigDouble? @base = null, bool linear = false)
        {
            return value.penta_log(@base, 100, linear);
        }

        /// <summary>Penta-root (see instance method).</summary>
        public static BigDouble linear_penta_root(BigDouble value, double degree)
        {
            return value.linear_penta_root(degree);
        }

        #endregion

        #region Trigonometry

        /// <summary>Sine.</summary>
        public static BigDouble sin(BigDouble value)
        {
            return value.sin();
        }

        /// <summary>Cosine.</summary>
        public static BigDouble cos(BigDouble value)
        {
            return value.cos();
        }

        /// <summary>Tangent.</summary>
        public static BigDouble tan(BigDouble value)
        {
            return value.tan();
        }

        /// <summary>Arcsine.</summary>
        public static BigDouble asin(BigDouble value)
        {
            return value.asin();
        }

        /// <summary>Arccosine.</summary>
        public static BigDouble acos(BigDouble value)
        {
            return value.acos();
        }

        /// <summary>Arctangent.</summary>
        public static BigDouble atan(BigDouble value)
        {
            return value.atan();
        }

        /// <summary>Hyperbolic sine.</summary>
        public static BigDouble sinh(BigDouble value)
        {
            return value.sinh();
        }

        /// <summary>Hyperbolic cosine.</summary>
        public static BigDouble cosh(BigDouble value)
        {
            return value.cosh();
        }

        /// <summary>Hyperbolic tangent.</summary>
        public static BigDouble tanh(BigDouble value)
        {
            return value.tanh();
        }

        /// <summary>Inverse hyperbolic sine.</summary>
        public static BigDouble asinh(BigDouble value)
        {
            return value.asinh();
        }

        /// <summary>Inverse hyperbolic cosine.</summary>
        public static BigDouble acosh(BigDouble value)
        {
            return value.acosh();
        }

        /// <summary>Inverse hyperbolic tangent.</summary>
        public static BigDouble atanh(BigDouble value)
        {
            return value.atanh();
        }

        #endregion

        #region Renamed members of the previous C# port

        /// <summary>Renamed to <see cref="toStringWithDecimalPlaces"/>.</summary>
        [Obsolete("Use toStringWithDecimalPlaces.")]
        public readonly string toStringWithdecPlaces(int places)
        {
            return toStringWithDecimalPlaces(places);
        }

        /// <summary>Renamed to <see cref="mantissaWithDecimalPlaces"/>.</summary>
        [Obsolete("Use mantissaWithDecimalPlaces.")]
        public readonly double mantissaWithdecPlaces(int places)
        {
            return mantissaWithDecimalPlaces(places);
        }

        /// <summary>Renamed to <see cref="magnitudeWithDecimalPlaces"/>.</summary>
        [Obsolete("Use magnitudeWithDecimalPlaces.")]
        public readonly double magnitudeWithdecPlaces(int places)
        {
            return magnitudeWithDecimalPlaces(places);
        }

        /// <summary>Renamed to <see cref="slog_critical"/>.</summary>
        [Obsolete("Use slog_critical.")]
        public static double slogCritical(double @base, double height)
        {
            return slog_critical(@base, height);
        }

        /// <summary>Renamed to <see cref="tetrate_critical"/>.</summary>
        [Obsolete("Use tetrate_critical.")]
        public static double tetrateCritical(double @base, double height)
        {
            return tetrate_critical(@base, height);
        }

        #endregion
    }

    // ================================================================================================================
    // 6. SerializableBigDouble
    // ================================================================================================================

    /// <summary>
    /// A BigDouble stored as a lossless, culture independent string (<see cref="BigDouble.ToSaveString"/>).
    /// Recommended for save data classes that are written with Unity's JsonUtility (or any other serializer):
    /// <code>
    /// [Serializable] class SaveData { public SerializableBigDouble joule; }
    /// save.joule = joule;              // BigDouble -> SerializableBigDouble (implicit)
    /// joule = save.joule;              // SerializableBigDouble -> BigDouble (implicit)
    /// </code>
    /// JSON: <c>"joule":{"value":"(e^1)1000000000"}</c>, the same with JsonUtility and Newtonsoft Json.NET.
    /// A missing/empty value reads as 0.
    /// </summary>
    [Serializable]
    [DataContract]
    public struct SerializableBigDouble : IEquatable<SerializableBigDouble>
    {
#if UNITY_5_3_OR_NEWER
        [UnityEngine.SerializeField]
#endif
        [DataMember(Name = "value")]
        private string value;

        /// <summary>Stores the given BigDouble.</summary>
        public SerializableBigDouble(BigDouble number)
        {
            value = number.ToSaveString();
        }

        /// <summary>
        /// The stored text (null or empty means 0). Settable for serializers that only use public properties
        /// (XmlSerializer, System.Text.Json).
        /// </summary>
#if UNITY_5_3_OR_NEWER
        [UnityEngine.Scripting.Preserve] // only serializers call the setter: code stripping would remove it and lose the value
#endif
        public string SerializedValue
        {
            get => value;
            set => this.value = value;
        }

        /// <summary>
        /// The stored number. Throws <see cref="FormatException"/> if the stored text is corrupt;
        /// use <see cref="TryGetValue"/> to handle that case without exceptions.
        /// </summary>
#if NETCOREAPP3_0_OR_GREATER
        [System.Text.Json.Serialization.JsonIgnore]
#endif
        public BigDouble Value
        {
            get
            {
                if (string.IsNullOrEmpty(value))
                {
                    return BigDouble.Zero;
                }
                if (!BigDouble.TryParse(value, out var result))
                {
                    throw new FormatException("Stored BigDouble value '" + value + "' is corrupt.");
                }
                return result;
            }
        }

        /// <summary>Reads the stored number without throwing. Empty text gives 0 and returns true.</summary>
        public bool TryGetValue(out BigDouble result)
        {
            if (string.IsNullOrEmpty(value))
            {
                result = BigDouble.Zero;
                return true;
            }
            return BigDouble.TryParse(value, out result);
        }

        /// <summary>Wraps a BigDouble.</summary>
        public static implicit operator SerializableBigDouble(BigDouble number)
        {
            return new SerializableBigDouble(number);
        }

        /// <summary>Wraps a double.</summary>
        public static implicit operator SerializableBigDouble(double number)
        {
            return new SerializableBigDouble(number);
        }

        /// <summary>Unwraps the BigDouble (throws <see cref="FormatException"/> for corrupt text).</summary>
        public static implicit operator BigDouble(SerializableBigDouble stored)
        {
            return stored.Value;
        }

        /// <summary>Compares the stored numbers.</summary>
        public bool Equals(SerializableBigDouble other)
        {
            if (TryGetValue(out var a) && other.TryGetValue(out var b))
            {
                return a.Equals(b);
            }
            return string.Equals(value, other.value, StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is SerializableBigDouble other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return TryGetValue(out var number) ? number.GetHashCode() : (value ?? "").GetHashCode();
        }

        /// <summary>The stored text ("0" if empty).</summary>
        public override string ToString()
        {
            return string.IsNullOrEmpty(value) ? "0" : value;
        }
    }

    /// <summary>
    /// Converts a BigDouble to and from its lossless save text. Newtonsoft Json.NET finds this through the attribute on
    /// BigDouble, without either library referencing the other: <c>"joule":"(e^1)10000.08990511144"</c>. Numbers are
    /// accepted as well, so a field that used to be a double still reads its old saves. Invalid text throws.
    /// </summary>
#if UNITY_5_3_OR_NEWER
    [UnityEngine.Scripting.Preserve] // created through reflection only; must survive code stripping
#endif
    public sealed class BigDoubleTypeConverter : TypeConverter
    {
        /// <summary>Creates the converter.</summary>
#if UNITY_5_3_OR_NEWER
        [UnityEngine.Scripting.Preserve]
#endif
        public BigDoubleTypeConverter()
        {
        }

        private static bool IsNumber(Type type)
        {
            var code = Type.GetTypeCode(type);
            return !type.IsEnum && code >= TypeCode.SByte && code <= TypeCode.Decimal;
        }

        /// <inheritdoc/>
        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
        {
            return sourceType == typeof(string) || IsNumber(sourceType) || base.CanConvertFrom(context, sourceType);
        }

        /// <inheritdoc/>
        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            if (value is string text)
            {
                return BigDouble.Parse(text);
            }
            if (value != null && IsNumber(value.GetType()))
            {
                return new BigDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }
            return base.ConvertFrom(context, culture, value);
        }

        /// <inheritdoc/>
        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
        }

        /// <inheritdoc/>
        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is BigDouble number)
            {
                return number.ToSaveString();
            }
            return base.ConvertTo(context, culture, value, destinationType);
        }
    }

#if NETCOREAPP3_0_OR_GREATER
    /// <summary>System.Text.Json: stores a BigDouble as its save text (not compiled in Unity, which does not ship it).</summary>
    public sealed class BigDoubleJsonConverter : System.Text.Json.Serialization.JsonConverter<BigDouble>
    {
        /// <inheritdoc/>
        public override BigDouble Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            return reader.TokenType == System.Text.Json.JsonTokenType.Number ? new BigDouble(reader.GetDouble()) : BigDouble.Parse(reader.GetString());
        }

        /// <inheritdoc/>
        public override void Write(System.Text.Json.Utf8JsonWriter writer, BigDouble value, System.Text.Json.JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToSaveString());
        }
    }
#endif
}
