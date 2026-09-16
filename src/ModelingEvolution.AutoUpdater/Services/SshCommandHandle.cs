using Renci.SshNet;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// One remote command. The seam between <see cref="SshService"/> and SSH.NET's <see cref="SshCommand"/>, so the timeout and
    /// dispose ordering can be tested without a server (bug-019).
    /// </summary>
    internal interface ISshCommandHandle : IDisposable
    {
        /// <summary>Runs the command. Cancelling <paramref name="cancellationToken"/> signals the remote process to stop.</summary>
        Task ExecuteAsync(CancellationToken cancellationToken);

        Stream OutputStream { get; }

        /// <summary>Standard output, when it was not read through <see cref="OutputStream"/>.</summary>
        string Result { get; }

        string Error { get; }

        int? ExitStatus { get; }
    }

    /// <summary>
    /// Creates commands on a connection and owns the connections' lifetime.
    /// </summary>
    internal interface ISshCommandFactory
    {
        ISshCommandHandle Create(string commandText);
    }

    /// <summary>
    /// <see cref="ISshCommandHandle"/> over SSH.NET. It never sets <see cref="SshCommand.CommandTimeout"/>: the timeout is owned by
    /// the caller's token (bug-019 — in SSH.NET 2024.x that timer fired on a disposed client and killed the process).
    /// </summary>
    internal sealed class SshNetCommandHandle : ISshCommandHandle
    {
        private readonly SshCommand _command;

        public SshNetCommandHandle(SshCommand command)
        {
            _command = command ?? throw new ArgumentNullException(nameof(command));
        }

        public Task ExecuteAsync(CancellationToken cancellationToken) => _command.ExecuteAsync(cancellationToken);

        public Stream OutputStream => _command.OutputStream;

        public string Result => _command.Result;

        public string Error => _command.Error;

        public int? ExitStatus => _command.ExitStatus;

        public void Dispose() => _command.Dispose();
    }

    internal sealed class SshNetCommandFactory : ISshCommandFactory
    {
        private readonly SshClient _client;

        public SshNetCommandFactory(SshClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public ISshCommandHandle Create(string commandText) => new SshNetCommandHandle(_client.CreateCommand(commandText));
    }
}
