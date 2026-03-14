interface PopSectionProps {
  isPopping: boolean;
  onPop: () => void;
  onPopWithAck: () => void;
}

export const PopSection = ({ isPopping, onPop, onPopWithAck }: PopSectionProps) => {
  return (
    <div className="card">
      <h3>Pop Messages</h3>
      <button onClick={onPop} disabled={isPopping}>
        {isPopping ? 'Popping...' : 'Pop from Queue'}
      </button>
      <button onClick={onPopWithAck} disabled={isPopping}>
        {isPopping ? 'Popping...' : 'Pop with Ack'}
      </button>
    </div>
  );
};
