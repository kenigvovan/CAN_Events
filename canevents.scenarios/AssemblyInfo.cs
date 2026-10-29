using Atlas.XUnit;

// Atlas runs one embedded server per test class.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Copied there by CopyCanPartyMod in the csproj.
[assembly: AtlasMods("deps/canparty.zip")]
