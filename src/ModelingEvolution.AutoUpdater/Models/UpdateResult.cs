using System;
using System.Collections.Generic;
using ModelingEvolution.AutoUpdater.Services;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Status of an update operation
    /// </summary>
    public enum UpdateStatus
    {
        Success,              // Full success, all services healthy
        Failed,               // Failed with successful rollback
        PartialSuccess,       // Some services running, no backup
        RecoverableFailure    // Failed but backup available for manual recovery
    }

    /// <summary>
    /// Result of an update operation
    /// </summary>
    public record UpdateResult
    {
        public UpdateStatus Status { get; init; }
        public bool Success => Status == UpdateStatus.Success;
        public string? Version { get; init; }
        public string? PreviousVersion { get; init; }
        public DateTime UpdatedAt { get; init; }
        public IReadOnlyList<string> ExecutedScripts { get; init; } = new List<string>();
        public string? ErrorMessage { get; init; }
        public HealthCheckResult? HealthCheck { get; init; }
        public string? BackupId { get; init; }
        public bool RecoveryPerformed { get; init; }

        // Legacy constructor for backward compatibility
        public UpdateResult(
            bool success,
            string? version,
            DateTime updatedAt,
            IReadOnlyList<string> executedScripts,
            string? errorMessage = null)
        {
            Status = success ? UpdateStatus.Success : UpdateStatus.Failed;
            Version = version;
            UpdatedAt = updatedAt;
            ExecutedScripts = executedScripts;
            ErrorMessage = errorMessage;
        }

        // New constructor with full status support
        private UpdateResult() { }

        public static UpdateResult CreateSuccess(
            string version,
            string? previousVersion,
            IReadOnlyList<string> executedScripts,
            HealthCheckResult healthCheck,
            string? backupId = null)
        {
            return new UpdateResult
            {
                Status = UpdateStatus.Success,
                Version = version,
                PreviousVersion = previousVersion,
                UpdatedAt = DateTime.Now,
                ExecutedScripts = executedScripts,
                HealthCheck = healthCheck,
                BackupId = backupId
            };
        }

        public static UpdateResult CreatePartialSuccess(
            string version,
            string? previousVersion,
            IReadOnlyList<string> executedScripts,
            HealthCheckResult healthCheck)
        {
            return new UpdateResult
            {
                Status = UpdateStatus.PartialSuccess,
                Version = version,
                PreviousVersion = previousVersion,
                UpdatedAt = DateTime.Now,
                ExecutedScripts = executedScripts,
                HealthCheck = healthCheck,
                ErrorMessage = "Partial deployment - some services unhealthy"
            };
        }

        public static UpdateResult CreateFailed(
            string errorMessage,
            string? previousVersion = null,
            IReadOnlyList<string>? executedScripts = null,
            bool recoveryPerformed = false,
            string? backupId = null)
        {
            return new UpdateResult
            {
                Status = UpdateStatus.Failed,
                PreviousVersion = previousVersion,
                UpdatedAt = DateTime.Now,
                ExecutedScripts = executedScripts ?? new List<string>(),
                ErrorMessage = errorMessage,
                RecoveryPerformed = recoveryPerformed,
                BackupId = backupId
            };
        }

        public static UpdateResult CreateRecoverableFailure(
            string errorMessage,
            string? previousVersion = null,
            IReadOnlyList<string>? executedScripts = null,
            string? backupId = null)
        {
            return new UpdateResult
            {
                Status = UpdateStatus.RecoverableFailure,
                PreviousVersion = previousVersion,
                UpdatedAt = DateTime.Now,
                ExecutedScripts = executedScripts ?? new List<string>(),
                ErrorMessage = errorMessage,
                BackupId = backupId
            };
        }
    }
}