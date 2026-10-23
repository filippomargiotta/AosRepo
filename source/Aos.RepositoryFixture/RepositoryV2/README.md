# Repository diagnostics fixture v2

Fixture v2 extends the frozen v1 snapshot with an API endpoint and its service registration,
recorded dependency evidence, and a recorded replay-mismatch report. The v1 files are unchanged.
The v2 additions are static diagnostic evidence only: they are not compiled, and no test profile
is bound to this snapshot, so every v2 case is answered by read-only search and file inspection.
