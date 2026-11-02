using Rankings.Job.Storage;

switch (args)
{
    case ["init-db", var path]:
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} already exists; delete it first to start over.");
            return 1;
        }
        using (RankingsDb.Open(path)) { }
        Console.WriteLine($"Created {path} (schema version {RankingsDb.SchemaVersion}).");
        return 0;

    default:
        Console.Error.WriteLine("Usage: Rankings.Job init-db <path>");
        return 2;
}
