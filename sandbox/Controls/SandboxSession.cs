using System;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;
using OpenRTS.Network;

// Concrete bridge for the existing sandbox's offline and direct-network consumers.
internal sealed class SandboxSession : IDisposable
{
    private readonly SimulationSession? _offline;
    internal StarSession? Network { get; }
    internal ReplaySession? Replay { get; }
    internal LocalReplayRecorder? Recorder { get; }
    internal SandboxSession(SimulationSession offline, LocalReplayRecorder? recorder = null) => (_offline, Recorder) = (offline, recorder);
    internal SandboxSession(StarSession network) => Network = network;
    internal SandboxSession(ReplaySession replay) => Replay = replay;
    public FrameExchange Frames => Replay?.Frames ?? Network?.Frames ?? _offline!.Frames;
    public System.Threading.Tasks.Task<SimulationReport> Completion => Replay?.Completion ?? Network?.Completion ?? _offline!.Completion;
    public double PlaybackSpeed => Replay?.PlaybackSpeed ?? _offline?.PlaybackSpeed ?? 1;
    public bool IsPaused => Replay is { } r ? r.IsPaused : Network is { } n ? n.State.Phase == MatchPhase.Paused : _offline!.IsPaused;
    public long Submit(WorldCommand command)
    {
        if (Replay is not null) throw new InvalidOperationException("Повтор доступен только для просмотра.");
        if (Recorder is not null) LocalReplayRecorder.ValidateInput(command);
        return Network?.Submit(command) ?? _offline!.Submit(command);
    }
    public long Submit(CommandKind kind, WorldPosition position = default, EntityId entity = default, int unitType = 0,
        uint owner = 0, int animation = 0, int durationTicks = 1, AnimationTrack track = AnimationTrack.Weapon,
        uint states = 0, EntityId targetEntity = default, EntitySet? recipients = null, bool queued = false,
        string? action = null, string? queue = null, int amount = 1, bool repeat = false, bool enabled = false,
        long taskId = 0, int quarterTurns = 0) => Submit(new WorldCommand(0,0,0,kind,entity,position,unitType,owner,
            animation,durationTicks,track,states,targetEntity,recipients,queued,action,queue,amount,repeat,enabled,taskId) { QuarterTurns = quarterTurns });
    public long Issue(UnitAction action) => Submit(SimulationSession.ToCommand(action));
    public long AttackContact(EntitySet recipients, uint viewer, long contactId, bool queued = false) =>
        Submit(new WorldCommand(0,0,0,CommandKind.AttackContact,recipients.Ids[0],Owner:viewer,Recipients:recipients,Queued:queued) { ContactId=contactId });
    public float GetInterpolationAlpha(long tick) => Replay?.GetInterpolationAlpha(tick) ?? Network?.GetInterpolationAlpha(tick) ?? _offline!.GetInterpolationAlpha(tick);
    public bool TryReadEvent(out WorldEvent e) { if (Replay is { } replay) return replay.TryReadEvent(out e); if (Network is { } n) return n.TryReadEvent(out e); return _offline!.TryReadEvent(out e); }
    public void SetPlayback(double speed, bool paused) { if (Replay is not null) Replay.SetPlayback(speed, paused); else if (_offline is not null) _offline.SetPlayback(speed,paused); }
    public void StepOnce() { if (Replay is not null) Replay.StepOnce(); else if (_offline is not null) _offline.StepOnce(); }
    public WorldCommand[] GetCommandLog() => _offline?.GetCommandLog() ?? throw new InvalidOperationException("Сетевой журнал записан в файл матча.");
    public void Dispose() { try { Replay?.Dispose(); Network?.Dispose(); _offline?.Dispose(); } finally { Recorder?.Dispose(); } }
}
