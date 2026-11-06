# Task 8 report
Set ProcessFeatures: ScanLoop=false (p95 0.526 ms <= 15), DiskBufferMultiplier=1 (chosen 64 MB == DefaultBufferSizeMB = max(64, 2*cores), 64 on the 4-core runner), DiskColumns=true (elevated, share 1.0051 >= 0.90). Added the "Processes (0.13.0, 2026-11-06)" top section to docs/validation.md with run link, numbers, decisions, share-above-1.0 note, and a placeholder "Release notes" subsection for Task 19.
Existing tests: DiagnosticOptionsTests and EtwDiskTests compare against ProcessFeatures dynamically; ProcessMeasurementTests gates on DiskColumns. No test hardcodes the old values; none changed.
Verification: solution build 0 errors (14 pre-existing warnings, none in touched files); Core suite 735 passed, 26 failed (baseline Linux), 1 skipped.
