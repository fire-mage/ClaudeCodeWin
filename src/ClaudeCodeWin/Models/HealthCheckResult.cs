namespace ClaudeCodeWin.Models;

public enum HealthStatus { Checking, OK, Warning, Error }

public record HealthCheckResult(string Name, HealthStatus Status, string Details);
