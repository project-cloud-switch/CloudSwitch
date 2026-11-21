namespace CloudSwitch.Worker.Doctor;

public sealed class DoctorOptions
{
    public int PollIntervalSeconds { get; set; } = 5;
    public int FreshnessMultiplier { get; set; } = 3;
    public bool Enabled { get; set; } = true;
}
