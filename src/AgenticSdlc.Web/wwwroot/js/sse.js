// Server-sent-events connector. The server emits named events (event: <AuditEventType>); we register
// the known set and route them all to one handler. Correctness comes from "refetch on event", so the
// handler only needs to know *that* something changed, not the full payload.

const EVENT_TYPES = [
  'WorkflowCreated', 'WorkflowStarted', 'WorkflowPaused', 'WorkflowResumed', 'WorkflowCancelled',
  'WorkflowCompleted', 'WorkflowFailed', 'NodeReady', 'NodeStarted', 'NodeSucceeded', 'NodeFailed',
  'NodeRetryScheduled', 'NodeTimedOut', 'NodeStale', 'NodeSkipped', 'NodeRecoveredAfterRestart',
  'GateEvaluated', 'ApprovalRequested', 'ApprovalGranted', 'ApprovalRejected', 'ApprovalVoided',
  'ClarificationRequested', 'ClarificationAnswered', 'ArtifactCreated', 'ArtifactApproved',
  'ArtifactSuperseded', 'DecisionRecorded', 'RiskRecorded', 'ReplanTriggered', 'RollbackTriggered',
  'LlmCallCompleted', 'ValidationRun', 'ReviewPackageAssembled',
];

export function connectEvents({ workflowId, onOpen, onEvent, onLive }) {
  const url = '/api/events' + (workflowId ? `?workflowId=${encodeURIComponent(workflowId)}` : '');
  const es = new EventSource(url);

  es.addEventListener('open', () => { onLive?.(true); onOpen?.(); });
  es.addEventListener('error', () => onLive?.(false));
  es.addEventListener('heartbeat', () => onLive?.(true));

  for (const type of EVENT_TYPES) {
    es.addEventListener(type, (e) => {
      let data = null;
      try { data = JSON.parse(e.data); } catch { /* ignore */ }
      onEvent?.(type, data);
    });
  }
  return es;
}

// Debounce helper to coalesce bursts of events into a single refresh.
export function debounce(fn, ms = 180) {
  let t;
  return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
}
