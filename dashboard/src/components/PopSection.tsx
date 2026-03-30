import { useState } from 'react';

interface PopSectionProps {
  isPopping: boolean;
  onPop: (count: number) => void;
  onPopWithAck: (count: number, ttl: number, competing: boolean) => void;
}

export const PopSection = ({ isPopping, onPop, onPopWithAck }: PopSectionProps) => {
  const [allowCompeting, setAllowCompeting] = useState(false);
  const [ttl, setTtl] = useState(30);
  const [popCount, setPopCount] = useState(1);

  const isValid = !allowCompeting || ttl >= 5;

  const handlePop = () => {
    onPop(popCount);
  };

  const handlePopWithAck = () => {
    onPopWithAck(popCount, ttl, allowCompeting);
  };

  return (
    <>
      {/* Pop from Queue Card */}
      <div className="card">
        <h3>Pop</h3>
        <p style={{ fontSize: '0.9em', color: '#666', marginBottom: '1rem' }}>
          Remove and retrieve the next message (immediate removal)
        </p>

        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
            <span>Count:</span>
            <select
              value={popCount}
              onChange={(e) => setPopCount(Number(e.target.value))}
              disabled={isPopping}
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

          <button onClick={handlePop} disabled={isPopping}>
            {isPopping ? 'Popping...' : 'Pop from Queue'}
          </button>
        </div>
      </div>

      {/* Pop with Acknowledgement Card */}
      <div className="card">
        <h3>Pop (Requires Acknowledgement)</h3>
        <p style={{ fontSize: '0.9em', color: '#666', marginBottom: '1rem' }}>
          Lock a message for processing (requires acknowledgement)
        </p>

        <div style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
            <span>Count:</span>
            <select
              value={popCount}
              onChange={(e) => setPopCount(Number(e.target.value))}
              disabled={isPopping}
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
              disabled={isPopping}
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
              disabled={isPopping}
              style={{ width: '80px' }}
            />
            <span>seconds</span>
          </label>

          {!isValid && (
            <div style={{ color: '#d32f2f', fontSize: '0.9em' }}>
              ⚠️ Competing consumer mode requires TTL ≥ 5 seconds
            </div>
          )}

          <button onClick={handlePopWithAck} disabled={isPopping || !isValid}>
            {isPopping ? 'Popping...' : 'Pop with Ack'}
          </button>
        </div>
      </div>
    </>
  );
};
