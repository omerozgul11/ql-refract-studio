using System.Globalization;

namespace RefractionStudio.Domain.Units;

/// <summary>Display unit system. Calculations are always SI internally.</summary>
public enum UnitSystem
{
    /// <summary>m, m/s, kg/m³, MPa/GPa.</summary>
    SI,

    /// <summary>ft, ft/s, pcf, psi/ksf.</summary>
    USCustomary,
}

/// <summary>Physical quantity kinds that appear in the UI and reports.</summary>
public enum QuantityKind
{
    Length,
    Depth,
    Elevation,
    Velocity,
    Time,
    Density,
    Modulus,
    Frequency,
    Slowness,
    Dimensionless,
}

/// <summary>How moduli are displayed.</summary>
public enum ModulusDisplayUnit
{
    MPa,
    GPa,
    Psi,
    Ksf,
}

/// <summary>Exact conversion factors (to SI). Sources: NIST SP 811 (2008) Appendix B.</summary>
public static class ConversionFactors
{
    /// <summary>International foot, exact.</summary>
    public const double MetresPerFoot = 0.3048;

    /// <summary>Avoirdupois pound, exact.</summary>
    public const double KilogramsPerPound = 0.45359237;

    /// <summary>Standard acceleration of gravity, exact (for pound-force).</summary>
    public const double StandardGravity = 9.80665;

    /// <summary>One pound per cubic foot in kg/m³ (0.45359237 / 0.3048³).</summary>
    public const double KgPerM3PerPcf = KilogramsPerPound / (MetresPerFoot * MetresPerFoot * MetresPerFoot);

    /// <summary>One pound-force in newtons.</summary>
    public const double NewtonsPerPoundForce = KilogramsPerPound * StandardGravity;

    /// <summary>One psi (lbf/in²) in pascals.</summary>
    public const double PascalsPerPsi = NewtonsPerPoundForce / (0.0254 * 0.0254);

    /// <summary>One ksf (1000 lbf/ft²) in pascals.</summary>
    public const double PascalsPerKsf = 1000.0 * NewtonsPerPoundForce / (MetresPerFoot * MetresPerFoot);

    /// <summary>One g/cm³ in kg/m³.</summary>
    public const double KgPerM3PerGramPerCm3 = 1000.0;
}

/// <summary>Unit conversion between SI and display units. Pure functions; no rounding.</summary>
public static class UnitConversion
{
    public static double MetresToFeet(double metres) => metres / ConversionFactors.MetresPerFoot;
    public static double FeetToMetres(double feet) => feet * ConversionFactors.MetresPerFoot;
    public static double KgM3ToPcf(double kgM3) => kgM3 / ConversionFactors.KgPerM3PerPcf;
    public static double PcfToKgM3(double pcf) => pcf * ConversionFactors.KgPerM3PerPcf;
    public static double KgM3ToGcm3(double kgM3) => kgM3 / ConversionFactors.KgPerM3PerGramPerCm3;
    public static double Gcm3ToKgM3(double gcm3) => gcm3 * ConversionFactors.KgPerM3PerGramPerCm3;
    public static double PascalsToPsi(double pa) => pa / ConversionFactors.PascalsPerPsi;
    public static double PsiToPascals(double psi) => psi * ConversionFactors.PascalsPerPsi;
    public static double PascalsToKsf(double pa) => pa / ConversionFactors.PascalsPerKsf;
    public static double KsfToPascals(double ksf) => ksf * ConversionFactors.PascalsPerKsf;

    /// <summary>Converts an SI value of the given kind to the display unit system.</summary>
    public static double ToDisplay(double siValue, QuantityKind kind, UnitSystem system,
        ModulusDisplayUnit modulusUnit = ModulusDisplayUnit.MPa) => kind switch
    {
        QuantityKind.Length or QuantityKind.Depth or QuantityKind.Elevation or QuantityKind.Velocity
            => system == UnitSystem.SI ? siValue : MetresToFeet(siValue),
        QuantityKind.Slowness => system == UnitSystem.SI ? siValue * 1000.0 : siValue * ConversionFactors.MetresPerFoot * 1000.0,
        QuantityKind.Time => siValue * 1000.0,
        QuantityKind.Density => system == UnitSystem.SI ? siValue : KgM3ToPcf(siValue),
        QuantityKind.Modulus => ModulusToDisplay(siValue, system, modulusUnit),
        _ => siValue,
    };

    /// <summary>Converts a display value back to SI (inverse of <see cref="ToDisplay"/>).</summary>
    public static double FromDisplay(double displayValue, QuantityKind kind, UnitSystem system,
        ModulusDisplayUnit modulusUnit = ModulusDisplayUnit.MPa) => kind switch
    {
        QuantityKind.Length or QuantityKind.Depth or QuantityKind.Elevation or QuantityKind.Velocity
            => system == UnitSystem.SI ? displayValue : FeetToMetres(displayValue),
        QuantityKind.Slowness => system == UnitSystem.SI ? displayValue / 1000.0 : displayValue / (ConversionFactors.MetresPerFoot * 1000.0),
        QuantityKind.Time => displayValue / 1000.0,
        QuantityKind.Density => system == UnitSystem.SI ? displayValue : PcfToKgM3(displayValue),
        QuantityKind.Modulus => ModulusFromDisplay(displayValue, system, modulusUnit),
        _ => displayValue,
    };

    /// <summary>The modulus unit actually used for a system (falls back to a unit of that system).</summary>
    public static ModulusDisplayUnit EffectiveModulusUnit(UnitSystem system, ModulusDisplayUnit requested) => system switch
    {
        UnitSystem.SI => requested is ModulusDisplayUnit.MPa or ModulusDisplayUnit.GPa ? requested : ModulusDisplayUnit.MPa,
        _ => requested is ModulusDisplayUnit.Psi or ModulusDisplayUnit.Ksf ? requested : ModulusDisplayUnit.Ksf,
    };

    private static double ModulusToDisplay(double pa, UnitSystem system, ModulusDisplayUnit unit)
        => EffectiveModulusUnit(system, unit) switch
        {
            ModulusDisplayUnit.MPa => pa / 1e6,
            ModulusDisplayUnit.GPa => pa / 1e9,
            ModulusDisplayUnit.Psi => PascalsToPsi(pa),
            _ => PascalsToKsf(pa),
        };

    private static double ModulusFromDisplay(double value, UnitSystem system, ModulusDisplayUnit unit)
        => EffectiveModulusUnit(system, unit) switch
        {
            ModulusDisplayUnit.MPa => value * 1e6,
            ModulusDisplayUnit.GPa => value * 1e9,
            ModulusDisplayUnit.Psi => PsiToPascals(value),
            _ => KsfToPascals(value),
        };

    /// <summary>The display symbol of a quantity kind.</summary>
    public static string Symbol(QuantityKind kind, UnitSystem system, ModulusDisplayUnit modulusUnit = ModulusDisplayUnit.MPa) => kind switch
    {
        QuantityKind.Length or QuantityKind.Depth or QuantityKind.Elevation => system == UnitSystem.SI ? "m" : "ft",
        QuantityKind.Velocity => system == UnitSystem.SI ? "m/s" : "ft/s",
        QuantityKind.Slowness => system == UnitSystem.SI ? "ms/m" : "ms/ft",
        QuantityKind.Time => "ms",
        QuantityKind.Density => system == UnitSystem.SI ? "kg/m³" : "pcf",
        QuantityKind.Modulus => EffectiveModulusUnit(system, modulusUnit) switch
        {
            ModulusDisplayUnit.MPa => "MPa",
            ModulusDisplayUnit.GPa => "GPa",
            ModulusDisplayUnit.Psi => "psi",
            _ => "ksf",
        },
        QuantityKind.Frequency => "Hz",
        _ => "",
    };
}

/// <summary>Formats SI values for display. Rounding happens only here.</summary>
public sealed record UnitFormatter(UnitSystem System, ModulusDisplayUnit ModulusUnit = ModulusDisplayUnit.MPa)
{
    public static UnitFormatter SI { get; } = new(UnitSystem.SI);

    public double ToDisplay(double si, QuantityKind kind) => UnitConversion.ToDisplay(si, kind, System, ModulusUnit);

    public double FromDisplay(double display, QuantityKind kind) => UnitConversion.FromDisplay(display, kind, System, ModulusUnit);

    public string Symbol(QuantityKind kind) => UnitConversion.Symbol(kind, System, ModulusUnit);

    /// <summary>Formats with a sensible default precision for the quantity.</summary>
    public string Format(double si, QuantityKind kind, int? decimals = null, bool withUnit = true)
    {
        if (double.IsNaN(si) || double.IsInfinity(si)) return "—";
        var value = ToDisplay(si, kind);
        var d = decimals ?? DefaultDecimals(kind, value);
        var text = value.ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var symbol = Symbol(kind);
        return withUnit && symbol.Length > 0 ? $"{text} {symbol}" : text;
    }

    private static int DefaultDecimals(QuantityKind kind, double value) => kind switch
    {
        QuantityKind.Velocity => 0,
        QuantityKind.Density => 0,
        QuantityKind.Time => 2,
        QuantityKind.Length or QuantityKind.Depth or QuantityKind.Elevation => 2,
        QuantityKind.Modulus => Math.Abs(value) >= 100 ? 0 : Math.Abs(value) >= 1 ? 1 : 3,
        QuantityKind.Frequency => 1,
        QuantityKind.Slowness => 4,
        _ => 3,
    };
}
