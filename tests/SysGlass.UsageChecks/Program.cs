int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}

string scratch = Path.Combine(Path.GetTempPath(), "SysGlass-usage-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    MemoryChecks.Run(Check);
    MemoryDiagnosticChecks.Run(scratch, Check);
}
finally { Directory.Delete(scratch, recursive: true); }

Console.WriteLine($"PASS: {checks} checks");
