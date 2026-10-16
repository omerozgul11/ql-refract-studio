namespace RefractionStudio.Infrastructure;

/// <summary>A context-help entry: what a control does, when to use it and when not to.</summary>
public sealed record HelpTopic(string Id, string Title, string WhatItDoes, string WhenToUse, string WhenNotToUse, string? Details = null)
{
    /// <summary>Short text for tooltips.</summary>
    public string Tooltip => WhatItDoes;
}

/// <summary>All context-help topics. Keys are referenced by the user interface.</summary>
public static class HelpCatalog
{
    private static readonly Dictionary<string, HelpTopic> Topics = new HelpTopic[]
    {
        // Workflow
        new("mode.guided", "Guided and Expert modes",
            "Guided mode shows the recommended settings and explanations; Expert mode exposes every algorithm parameter.",
            "Use Guided mode for occasional surveys or when learning the workflow.",
            "Switching mode never changes the project; it only changes what is shown.",
            "Both modes work on the same project file. Settings changed in Expert mode remain in effect in Guided mode and are listed in the report."),
        new("workflow", "Workflow",
            "The steps on the left track the progress of the interpretation: import, geometry, processing, picking, quality control, velocity model, engineering results and report.",
            "Follow the steps in order the first time; jump back at any point to change something — results update automatically.",
            "The step status is guidance, not a requirement: no step is enforced."),

        // Pages
        new("import", "Importing seismic records",
            "Reads SEG-2 and SEG-Y shot records, shows what each file contains and lists problems (unreadable files, inconsistent sampling, missing positions) with proposed corrections that you approve. The original files are stored unchanged inside the project.",
            "Import the records of one survey line (P or SH) at a time; choose how records at the same shot position are combined (separate, stacked, or SH left/right pairs).",
            "Do not import records of different lines into one line: geometry, processing and picks are kept per line."),
        new("geometry", "Survey geometry",
            "Defines the receiver spread (first position, spacing, channel order, channel roles such as Reference or Unused), the shot positions and depths, and the ground profile used for elevations.",
            "Check it after every import: positions from file headers are used when present, otherwise the spacing you entered. Every result updates when geometry changes.",
            "Do not use channel roles to hide bad traces: exclude them on the Waveforms page instead, so the decision is recorded."),
        new("waveforms", "Inspecting waveforms",
            "Displays each shot gather (wiggle, variable area or image) with per-trace or common scaling, gain, clipping and an optional reduction velocity, and the amplitude spectrum of the shot.",
            "Look at every shot before picking: find dead, noisy or reversed traces and correct them in the trace inspector (exclude, reverse polarity, shift, gain).",
            "Display gain, clipping and reduction never change the data or the picks."),
        new("report", "Report",
            "Generates the PDF report from the project: cover, summary, survey, processing, gathers, travel times, models, profiles, engineering results, quality control, warnings and appendices, with your branding and conclusions.",
            "Fill in the report details and conclusions, choose the sections and units, then generate. Tables are also exported to XLSX/CSV from the Results page.",
            "The report never writes engineering conclusions for you, and its quality summary does not certify compliance with any standard."),

        // Processing
        new("processing.removeMean", "Remove DC offset (demean)",
            "Subtracts the average value of each trace so that it oscillates around zero.",
            "Almost always; it is harmless and makes filters and AGC behave predictably.",
            "Not needed for data that are already zero-mean.",
            "When a strong low-frequency arrival dominates the record, the mean is not zero before the first break; the pickers compare energy with the local background and are not affected."),
        new("processing.detrend", "Linear detrend",
            "Removes the best-fitting straight line from each trace.",
            "When traces drift (sloping baseline), e.g. from instrument settling.",
            "On short records that contain mostly signal, a fitted line may remove real low-frequency energy."),
        new("processing.taper", "Taper",
            "Smoothly reduces the amplitude at both ends of each trace.",
            "Before filtering, to avoid ringing from the abrupt start and end of the record.",
            "If first arrivals occur within the tapered part at the start of the record (very near-shot traces), use a small fraction."),
        new("processing.highPass", "High-pass filter",
            "Removes frequencies below the corner frequency (Butterworth).",
            "To suppress low-frequency surface waves and wind noise that obscure first breaks. Prefer causal (minimum-phase) filtering while picking.",
            "Do not set the corner near the dominant frequency of the first arrivals; it changes their shape.",
            "Causal filters cannot move energy ahead of the true onset. Zero-phase filters have no phase shift but spread energy both forwards and backwards in time, which can make first breaks look early."),
        new("processing.lowPass", "Low-pass filter",
            "Removes frequencies above the corner frequency (Butterworth).",
            "To reduce high-frequency noise for display.",
            "A causal low-pass delays arrivals (its group delay, shown in the processing notes); picks made on it are late by that amount. Use zero-phase for display only, or pick on unfiltered data."),
        new("processing.bandPass", "Band-pass filter",
            "Keeps a frequency band (Butterworth high-pass and low-pass in cascade).",
            "To isolate the signal band after inspecting the spectrum; the spectrum view suggests corners.",
            "See the high- and low-pass notes: the low-pass part delays causal picks; zero-phase filtering can make picks early."),
        new("processing.notch", "Notch filter",
            "Removes a narrow band around one frequency and optionally its harmonics.",
            "Power-line interference (50 or 60 Hz) visible as a spectral spike.",
            "If no clear spike is visible in the spectrum."),
        new("processing.normalize", "Normalize",
            "Scales each trace to unit peak or RMS amplitude.",
            "For comparing waveforms of traces with very different amplitudes.",
            "Not for amplitude analysis: relative amplitudes between traces are lost."),
        new("processing.balance", "Trace balance",
            "Scales each trace so that its RMS amplitude in a chosen time window is one.",
            "To equalise traces using a window that contains the arrivals of interest.",
            "If the window contains no signal on some traces, their noise is amplified."),
        new("processing.agc", "Automatic gain control",
            "Divides each sample by the RMS amplitude of a sliding window, making weak and strong parts look similar.",
            "As a display aid for weak far-offset arrivals.",
            "Never for amplitude interpretation; AGC also raises noise before the first break, which lowers pick confidence."),
        new("processing.polarity", "Polarity reversal",
            "Multiplies every trace by −1.",
            "When the whole record has inverted polarity (e.g. reversed geophone cables).",
            "For a single miswired geophone, reverse only that trace (trace properties) or its channel (Geometry)."),
        new("processing.timeShift", "Time shift",
            "Moves all samples later (positive) or earlier (negative). Analysis times include the shift.",
            "To correct a known constant timing error of the recording system.",
            "For per-shot trigger errors use shot-time correction; for one trace use the trace time shift."),
        new("processing.resample", "Resample",
            "Changes the sample interval using band-limited interpolation; decimation is preceded by an anti-alias filter.",
            "To reduce very large records or to interpolate for display.",
            "Do not decimate below twice the highest signal frequency; first-break precision is limited by the sample interval."),

        // Picking
        new("picking.aic", "AIC picker",
            "Finds the onset as the point that best splits the trace into a 'noise' part and a 'signal' part (Akaike information criterion), inside a window around the first energy rise.",
            "Default picker; precise onsets on clear and moderately noisy data.",
            "Very emergent arrivals in strong noise: check against the other pickers (the confidence shows their agreement)."),
        new("picking.staLta", "STA/LTA picker",
            "Compares short-term with long-term signal energy and picks where the ratio starts to rise.",
            "Robust detection of arrivals in noisy records.",
            "Its onset estimate is less precise than AIC (often a sample or two early or late); use it to confirm AIC picks."),
        new("picking.energyRatio", "Modified energy ratio picker",
            "Uses the ratio of energy after and before each sample, weighted by amplitude, to locate the onset.",
            "An independent check of the onset on impulsive arrivals.",
            "Long emergent wavelets; low signal-to-noise ratio."),
        new("picking.sh", "SH polarity-divergence picker",
            "For gathers combined from left and right impacts: picks where the energy that reverses with the impact direction (shear) begins.",
            "SH surveys with opposite impacts; it ignores P energy that does not reverse.",
            "Single-direction SH records (no opposite impact) — use AIC and confirm visually."),
        new("picking.confidence", "Pick confidence",
            "A 0–1 score from signal-to-noise ratio, onset sharpness, consistency with neighbouring traces and agreement between independent pickers. High ≥ 0.7, Medium ≥ 0.4.",
            "Review Low picks first; the reasons explain each score.",
            "Confidence is not an error bar; a confident pick on the wrong arrival is still wrong."),
        new("picking.manual", "Manual picking",
            "Click a trace to place a pick; drag to move; arrow keys move one sample (Shift: ten); Enter accepts the suggestion; Delete removes the pick.",
            "To review and correct automatic picks.",
            "Manual picks are never overwritten by automatic picking; the automatic suggestion is kept for comparison."),
        new("picking.correlation", "Pick transfer by cross-correlation",
            "Copies a reviewed pick to neighbouring traces by aligning their waveforms.",
            "Consistent waveforms along the spread; fast picking of long records.",
            "Where the waveform changes (crossovers, noise); transfer stops automatically when correlation drops."),

        // Shot time
        new("shotTime.reference", "Reference-geophone correction",
            "Corrects each shot's time zero using the arrival on a geophone at (or near) the source, picked on the unprocessed trace.",
            "When the trigger delay varies from shot to shot (hammer switches, geophone triggers).",
            "Without a reliable reference channel. Raw times are always kept; corrections are listed per shot."),
        new("shotTime.manual", "Manual shot-time correction",
            "Applies a correction entered by you to all picks of a shot.",
            "Known timing errors (e.g. from field notes) or after investigating the trigger flag in QC.",
            "Do not use it to make travel-time curves look better; document the reason in the note."),

        // Layers and methods
        new("layers.assignment", "Layer assignment",
            "Decides which refractor each first arrival belongs to (1 = direct wave). Crossover distances can be dragged on the travel-time plot.",
            "Always review the automatic suggestion before trusting a layered model.",
            "Do not create more layers than the data show; each layer needs several arrivals from several shots."),
        new("method.interceptTime", "Intercept-time method",
            "Planar layers from the slopes and intercepts of travel-time segments; with a reversed shot pair a dipping refractor is solved exactly.",
            "Quick models of simple sites; depths beneath shot points.",
            "Irregular refractors: depth is only obtained beneath the shots."),
        new("method.plusMinus", "Plus–minus method (Hagedoorn)",
            "Combines forward and reverse arrivals at each geophone: the 'minus' time gives the refractor velocity, the 'plus' time its depth.",
            "Reversed spreads over moderately irregular refractors.",
            "Strong lateral velocity changes or steep dips; it is the GRM with XY = 0."),
        new("method.grm", "Generalized reciprocal method (Palmer)",
            "Like plus–minus but combines arrivals at two receivers XY apart, which sharpens depth and velocity estimates on irregular refractors.",
            "Detailed refractor mapping with dense geophones; choose XY with the XY analysis.",
            "Sparse data: a large XY needs arrivals on both sides of every position."),
        new("method.delayTime", "Delay-time (time-term) method",
            "Solves for a delay at every shot and receiver position and the refractor velocity simultaneously by least squares, using all shots.",
            "Lines with several shots including at least one inside the spread; gives uncertainties.",
            "Only end shots: source and receiver delays cannot be separated (a warning is shown)."),

        // Tomography
        new("tomography.overview", "Travel-time tomography",
            "Finds a smooth velocity model whose computed first arrivals match the picks, by ray tracing and regularised least squares.",
            "Gradational velocities, irregular interfaces, velocity reversals within limits; complements the layered model.",
            "Interpret only ray-covered cells (hatched cells are unconstrained). Smoothing blurs sharp interfaces."),
        new("tomography.smoothing", "Regularisation (λ)",
            "Controls how strongly the model is kept smooth. Larger λ: smoother model, larger misfit.",
            "Start with the default; lower it if the misfit target is not reached.",
            "Very small λ fits noise and produces artefacts."),
        new("tomography.cell", "Cell size",
            "Size of the model cells. Square cells give the most accurate ray tracing.",
            "About half the geophone spacing.",
            "Very small cells increase run time without adding resolution beyond the geophone spacing."),
        new("tomography.robust", "Robust data weighting",
            "Down-weights picks whose residuals are much larger than their uncertainty, so a few mis-picks cannot distort the model.",
            "Default; review the down-weighted picks listed in QC.",
            "It does not correct picks: fix or exclude outliers and run again."),
        new("tomography.start", "Starting model",
            "The model the inversion starts from: a velocity gradient, a constant velocity or the layered interpretation.",
            "Gradient for general use; layered when the layered model is reliable.",
            "Results that depend strongly on the starting model are poorly constrained — compare starting models using scenarios."),

        // Engineering
        new("vs30", "Vs30",
            "Time-averaged shear-wave velocity over the upper 30 m: 30 / Σ(hᵢ/Vsᵢ).",
            "Site classification inputs; requires an SH (shear) line.",
            "Where the model is constrained to less than 30 m no value is given, unless you deliberately enable extrapolation."),
        new("vs30.extrapolation", "Vs30 extrapolation",
            "Allows Vs30 where data reach less than 30 m by holding the deepest constrained velocity constant below it. Results are labelled EXTRAPOLATED everywhere.",
            "Only when a value is needed and the assumption is justified (e.g. bedrock known from boreholes).",
            "By default; extrapolated values are not measurements."),
        new("density", "Density",
            "Density is needed for moduli. Enter measured or assumed values by depth, or explicitly select an empirical estimate from Vp.",
            "Measured densities from laboratory or borehole data are preferred.",
            "Estimated densities (Gardner, Brocher) were calibrated for rocks; they are not valid for most soils and are labelled ESTIMATED."),
        new("engineering", "Engineering parameters",
            "Poisson's ratio, shear, Young's and bulk moduli from Vp, Vs and density (isotropic linear elasticity, small strain).",
            "When P and SH lines cover the same ground.",
            "Water-saturated soils: Vp reflects the pore fluid, so Poisson's ratio approaches 0.5 and K mainly reflects water."),

        // QC
        new("qc.overview", "Quality control",
            "Automatic checks of picks, timing, geometry and models. Green = acceptable, yellow = review, red = likely problem — always with an icon and text.",
            "Before generating the report.",
            "QC findings are aids to review; they do not certify compliance with any standard."),
        new("qc.reciprocity", "Reciprocity",
            "The travel time from A to B must equal that from B to A. Large differences indicate pick or trigger errors.",
            "Shots placed at geophone positions provide reciprocal pairs.",
            "Different source types at A and B can cause small genuine differences."),
        new("scenarios", "Scenarios",
            "Alternative interpretations of the same data (picks, processing, methods) that can be compared side by side.",
            "To assess sensitivity: manual vs automatic picks, different filters or methods.",
            "Scenarios share the raw data; geometry changes affect all scenarios."),
        new("units", "Units",
            "Display units (SI or US customary). Calculations always use SI internally; values are converted only for display.",
            "Choose the units of your deliverable.",
            "Changing units never changes stored values."),
    }.ToDictionary(t => t.Id);

    public static HelpTopic? Get(string id) => Topics.GetValueOrDefault(id);

    public static IReadOnlyCollection<HelpTopic> All => Topics.Values;

    /// <summary>Searches titles and text (command palette / help search).</summary>
    public static IReadOnlyList<HelpTopic> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [.. Topics.Values.OrderBy(t => t.Title)];
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. Topics.Values
            .Select(t => (Topic: t, Score: terms.Sum(term => (t.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ? 3 : 0) + (t.WhatItDoes.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Topic)];
    }

    /// <summary>The integrated user guide (Markdown).</summary>
    public static string UserGuide()
    {
        using var s = typeof(HelpCatalog).Assembly.GetManifestResourceStream("RefractionStudio.Help.user-guide.md");
        if (s is null) return "# User guide\n\nThe user guide is not included in this build.";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
