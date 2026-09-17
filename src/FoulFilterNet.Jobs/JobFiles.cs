using FoulFilterNet.Domain;

namespace FoulFilterNet.Jobs;

/// <summary>
/// Retires the files a job leaves behind. Every operation is best-effort: a
/// file that is already gone, or held open by something else, must not turn a
/// cancelled job into a failed one.
/// </summary>
internal static class JobFiles
{
    /// <summary>
    /// Removes the job's scratch directory and its upload. The output survives
    /// unless the job failed, matching the Python's <c>keep_output</c>.
    /// </summary>
    public static void Cleanup(JobRequest request, bool keepOutput)
    {
        TryDeleteDirectory(request.ScratchDirectory);
        TryDeleteFile(request.InputPath);

        if (!keepOutput)
        {
            TryDeleteFile(request.OutputPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
