namespace ModelScanner.Tasks
{
    interface IJobTask
    {
        JobTaskTypes TaskType { get; }

        Task<bool> Process(string filePath, ScanResult result, CancellationToken cancellationToken);
    }
}
