namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Finite state machine for ECU simulation. Given a UDS request, finds the matching
/// transition (based on current state + request fields), generates a response
/// (static or dynamic), and transitions to a new state.
/// </summary>
public sealed class EcuStateMachine
{
    private readonly List<EcuStateTransition> _transitions;
    private readonly Dictionary<string, IEcuResponseGenerator> _generators;
    private readonly EcuContextStore _context = new();
    private string _currentState = "default";

    /// <summary>Current ECU state name.</summary>
    public string CurrentState => _currentState;

    /// <summary>Shared context store for stateful data across requests.</summary>
    public IEcuContext Context => _context;

    /// <summary>
    /// Create a state machine from transitions and optional dynamic generators.
    /// </summary>
    public EcuStateMachine(
        IEnumerable<EcuStateTransition> transitions,
        IEnumerable<IEcuResponseGenerator>? generators = null)
    {
        _transitions = transitions.ToList();
        _generators = generators?.ToDictionary(g => g.Name) ?? new();
    }

    /// <summary>
    /// Process a UDS request: find matching transition, generate response,
    /// update state. Returns NRC 0x11 if no match.
    /// Returns (response, delayMs) tuple so caller can apply delay before sending.
    /// </summary>
    public (byte[] Response, int DelayMs) ProcessRequest(byte[] request)
    {
        if (request.Length == 0)
            return (new byte[] { 0x7F, 0x00, 0x13 }, 0); // NRC 0x13 incorrectMessageLength

        var sid = request[0];

        foreach (var t in _transitions)
        {
            if (!MatchesState(t) || !MatchesRequest(t, request))
                continue;

            // Generate response
            byte[] response = t.Response switch
            {
                StaticResponse s => s.Data,
                DynamicResponse d => _generators.TryGetValue(d.GeneratorName, out var gen)
                    ? gen.Generate(request, _currentState, _context)
                    : new byte[] { 0x7F, sid, 0x72 }, // NRC 0x72 generalProgrammingFailure
                _ => new byte[] { 0x7F, sid, 0x72 }
            };

            // State transition
            if (t.ToState is not null)
                _currentState = t.ToState;

            return (response, t.ResponseDelayMs);
        }

        // No matching transition -> NRC 0x11 (serviceNotSupported)
        return (new byte[] { 0x7F, sid, 0x11 }, 0);
    }

    /// <summary>
    /// Wildcard: null FromState matches any state.
    /// This avoids conflict with a user-defined state named "default".
    /// </summary>
    private bool MatchesState(EcuStateTransition t)
        => t.FromState is null || t.FromState == _currentState;

    private bool MatchesRequest(EcuStateTransition t, byte[] request)
    {
        if (request[0] != t.ServiceId)
            return false;

        if (t.SubFunction.HasValue && (request.Length < 2 || request[1] != t.SubFunction.Value))
            return false;

        if (t.DataMask is not null && t.DataMask.Length > 0)
        {
            if (request.Length < 2 + t.DataMask.Length)
                return false;
            for (int i = 0; i < t.DataMask.Length; i++)
            {
                if ((request[2 + i] & t.DataMask[i]) != t.DataPattern![i])
                    return false;
            }
        }

        return true;
    }

    /// <summary>Reset to initial state and clear context.</summary>
    public void Reset()
    {
        _currentState = "default";
        _context.Clear();
    }

    /// <summary>
    /// Convert stateless UdsResponseRule list to a stateful machine.
    /// All rules go into null-FromState (wildcard) transitions for backward compatibility.
    /// </summary>
    public static EcuStateMachine FromRules(IEnumerable<UdsResponseRule> rules)
    {
        var transitions = rules.Select(r => new EcuStateTransition
        {
            FromState = null, // wildcard: matches any state
            ServiceId = r.ServiceId,
            SubFunction = r.SubFunction,
            DataMask = r.DataMask,
            DataPattern = r.DataPattern,
            Response = new StaticResponse(r.ResponseData),
            ResponseDelayMs = r.ResponseDelayMs,
            ToState = null,
        });
        return new EcuStateMachine(transitions);
    }
}
