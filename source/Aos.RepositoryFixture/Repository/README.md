# Repository diagnostics fixture v1

This frozen miniature .NET repository contains an intentional discount calculation defect
and an environment-specific configuration override. Its pricing source and focused test
are compiled into Aos.RepositoryFixture and packaged with Aos.SandboxWorker.

The broad profile simulates a slow integration suite; the focused profile executes the
real pricing assertion. No profile builds, restores dependencies, or modifies this snapshot.
