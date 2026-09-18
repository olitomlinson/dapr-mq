import { useState } from 'react';
import styles from './TabContainer.module.css';

interface TabContainerProps {
  dequeuedCount: number;
  wiremockCount: number;
  children: [React.ReactNode, React.ReactNode]; // [messages tab, wiremock tab]
}

export const TabContainer = ({ dequeuedCount, wiremockCount, children }: TabContainerProps) => {
  const [activeTab, setActiveTab] = useState<'messages' | 'wiremock'>('messages');

  return (
    <div className={styles.tabContainer}>
      <div className={styles.tabButtons}>
        <button
          className={activeTab === 'messages' ? styles.tabActive : styles.tab}
          onClick={() => setActiveTab('messages')}
        >
          Dequeued Messages ({dequeuedCount})
        </button>
        <button
          className={activeTab === 'wiremock' ? styles.tabActive : styles.tab}
          onClick={() => setActiveTab('wiremock')}
        >
          WireMock ({wiremockCount})
        </button>
      </div>
      <div className={styles.tabContent}>
        {activeTab === 'messages' ? children[0] : children[1]}
      </div>
    </div>
  );
};
