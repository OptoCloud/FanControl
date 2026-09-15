namespace FanControl.Core.Fans;

public sealed record FanStatus(string Id, int DutyPercent, int? Rpm, PwmEnableMode Mode);
