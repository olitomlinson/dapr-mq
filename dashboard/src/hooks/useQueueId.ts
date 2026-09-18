import { useState } from 'react';
import { generateQueueId } from '../utils/queueHelpers';

export const useQueueId = (): string => {
  const [queueId] = useState(() => {
    const params = new URLSearchParams(window.location.search);
    return params.get('queue_name') || generateQueueId();
  });

  return queueId;
};
