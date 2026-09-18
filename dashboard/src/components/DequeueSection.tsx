import { useState } from 'react';

interface DequeueSectionProps {
  isDequeuing: boolean;
  onDequeue: (count: number) => void;
  onDequeueLocked: (count: number, ttl: number, competing: boolean) => void;
}

export const DequeueSection = ({ isDequeuing, onDequeue, onDequeueLocked }: DequeueSectionProps) => {
  const [allowCompeting, setAllowCompeting] = useState(false);
  const [ttl, setTtl] = useState(30);
  const [dequeueCount, setDequeueCount] = useState(1);

  const isValid = !allowCompeting || ttl >= 5;

  const handleDequeue = () => {
    onDequeue(dequeueCount);
  };

  const handleDequeueLocked = () => {
    onDequeueLocked(dequeueCount, ttl, allowCompeting);
  };

  return (
    <>
      {/* Dequeue from Queue Card */}
      <div className="card">
        <h3>Dequeue</h3>
        <p style={{ fontSize: '0.9em', color: '#666', marginBottom: '1rem' }}>
          Remove and retrieve the next message (immediate removal)
        </p>

        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
            <span>Count:</span>
            <select
              value={dequeueCount}
              onChange={(e) => setDequeueCount(Number(e.target.value))}
              disabled={isDequeuing}
              style={{ padding: '0.25rem 0.5rem' }}
            >
              <option value={1}>1</option>
              <option value={5}>5</option>
              <option value={10}>10</option>
              <option value={25}>25</option>
              <option value={50}>50</option>
              <option value={100}>100</option>
            </select>
          </label>

          <button onClick={handleDequeue} disabled={isDequeuing}>
            {isDequeuing ? 'Dequeuing...' : 'Dequeue from Queue'}
          </button>
        </div>
      </div>

      {/* Dequeue with Acknowledgement Card */}
      <div className="card">
        <h3>Dequeue (Requires Acknowledgement)</h3>
        <p style={{ fontSize: '0.9em', color: '#666', marginBottom: '1rem' }}>
          Lock a message for processing (requires acknowledgement)
        </p>

        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
            <span>Count:</span>
            <select
              value={dequeueCount}
              onChange={(e) => setDequeueCount(Number(e.target.value))}
              disabled={isDequeuing}
              style={{ padding: '0.25rem 0.5rem' }}
            >
              <option value={1}>1</option>
              <option value={5}>5</option>
              <option value={10}>10</option>
              <option value={25}>25</option>
              <option value={50}>50</option>
              <option value={100}>100</option>
            </select>
          </label>

          <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
            <input
              type="checkbox"
              checked={allowCompeting}
              onChange={(e) => setAllowCompeting(e.target.checked)}
              disabled={isDequeuing}
            />
            <span>Allow Competing Consumers</span>
          </label>

          <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
            <span>TTL:</span>
            <input
              type="number"
              value={ttl}
              onChange={(e) => setTtl(Number(e.target.value))}
              min={1}
              max={300}
              disabled={isDequeuing}
              style={{ width: '80px' }}
            />
            <span>seconds</span>
          </label>

          {!isValid && (
            <div style={{ color: '#d32f2f', fontSize: '0.9em' }}>
              ⚠️ Competing consumer mode requires TTL ≥ 5 seconds
            </div>
          )}

          <button onClick={handleDequeueLocked} disabled={isDequeuing || !isValid}>
            {isDequeuing ? 'Dequeuing...' : 'Dequeue with Ack'}
          </button>
        </div>
      </div>
    </>
  );
};
