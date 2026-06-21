using Xunit;

// Each serve fixture boots a server and sets the process-wide CIFAIL_HOME to isolate its
// SQLite history. Running the fixtures in parallel would let those env writes race, so keep
// this assembly's collections sequential.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
