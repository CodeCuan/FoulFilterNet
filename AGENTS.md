# Agent instructions

## Formatting

After editing any C# or MSBuild file (`.cs`, `.csproj`, `.props`), run CSharpier
from the repository root before you finish or commit:

```bash
csharpier format .
```

CSharpier is installed as a global .NET tool (`dotnet tool install -g csharpier`).
Keep formatting-only churn out of unrelated commits: if the formatter touches files
you did not edit, commit that separately.
