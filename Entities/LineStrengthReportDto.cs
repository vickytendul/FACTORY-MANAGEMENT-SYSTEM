namespace FactoryManagementSystem.Entities;

public class LineStrengthReportDto
{
    public int LineId { get; set; }
    public string LineNo { get; set; } = string.Empty;
    public int CCId { get; set; }
    public string CCNo { get; set; } = string.Empty;
    public int PlannedTailors { get; set; }

    public int TotalAllocated { get; set; }
    public int TotalPresent { get; set; }
    public int TotalAbsent { get; set; }
    public double TotalAbPercent { get; set; }

    public int TailorAllocated { get; set; }
    public int TailorPresent { get; set; }
    public int TailorAbsent { get; set; }
    public double TailorAbPercent { get; set; }

    public int OthersAllocated { get; set; }
    public int OthersPresent { get; set; }
    public int OthersAbsent { get; set; }
    public double OthersAbPercent { get; set; }

    public int SewingHelperAllocated { get; set; }
    public int SewingHelperPresent { get; set; }
    public int SewingHelperAbsent { get; set; }
    public double SewingHelperAbPercent { get; set; }

    public int LineLeaderAllocated { get; set; }
    public int LineLeaderPresent { get; set; }
    public int LineLeaderAbsent { get; set; }
    public double LineLeaderAbPercent { get; set; }

    public int CheckerAllocated { get; set; }
    public int CheckerPresent { get; set; }
    public int CheckerAbsent { get; set; }
    public double CheckerAbPercent { get; set; }

    public int PackingHelperAllocated { get; set; }
    public int PackingHelperPresent { get; set; }
    public int PackingHelperAbsent { get; set; }
    public double PackingHelperAbPercent { get; set; }

    public int SuperTeamAllocated { get; set; }
    public int SuperTeamPresent { get; set; }
    public int SuperTeamAbsent { get; set; }
    public double SuperTeamAbPercent { get; set; }
}
