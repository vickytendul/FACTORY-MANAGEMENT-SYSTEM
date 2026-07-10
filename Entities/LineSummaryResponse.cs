namespace FactoryManagementSystem.Entities
{
    public class LineSummaryResponse
    {
        public string CCNo { get; set; } = string.Empty;
        public double SAM { get; set; }

        public int TailorsOnRoll { get; set; }
        public int OthersOnRoll { get; set; }
        public int TotalOnRoll { get; set; }

        public int TailorsPresent { get; set; }
        public int OthersPresent { get; set; }
        public int TotalPresent { get; set; }

        public int Absent => TotalOnRoll - TotalPresent;

        public decimal Absenteeism =>
            TotalOnRoll == 0
                ? 0
                : Math.Round((decimal)Absent * 100 / TotalOnRoll, 2);
    }
}