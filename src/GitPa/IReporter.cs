namespace GitPa;

/// <summary>Receives progress from operations, so they stay independent of the console.</summary>
public interface IReporter
{
    void Step(string message);

    void Warn(string message);
}
