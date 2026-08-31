namespace PiAiTrader.Intelligence
{
    /// <summary>
    /// Shape of the shared aggregator-config.json file: a small JSON file
    /// both DualMomentumV2 (reader, via AggregatorConfigReader) and the web
    /// portal (writer, see web/routes/api.py) agree on. Deliberately minimal
    /// -- just the one field this session needs.
    /// </summary>
    public class AggregatorConfig
    {
        /// <summary>String name of the active AggregationMode, e.g.
        /// "CapitalSplit". Stored as a string (not the enum directly) so the
        /// JSON file stays human-readable/editable and so an unrecognized or
        /// future value doesn't fail JSON parsing itself -- only enum
        /// resolution, which AggregatorConfigReader already handles with a
        /// safe fallback.</summary>
        public string ActiveMode { get; set; }
    }
}
