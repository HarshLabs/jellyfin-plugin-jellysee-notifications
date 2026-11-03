These legacy root-layout fixtures were written by the unmodified 1.0.1 state
sources from origin/main 14c4ba9 (Journal, Protocol, State, CompactCodec,
StateDirectory, StateFailure and StateIoWorker), compiled in an isolated .NET 10
console harness. Each has one outbox add, one indexed title and Dropped=7 at
sequence 2. The snapshot variant compacts sequence 1 before appending health;
the journal variant never compacts. Tests import both without modifying either
source file, reopen for downgrade, and resume the isolated 1.1 state.
