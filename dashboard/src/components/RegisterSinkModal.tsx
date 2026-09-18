import { useState, useEffect, useRef } from 'react';
import type { RegisterSinkRequest } from '../types/queue';
import styles from './RegisterSinkModal.module.css';

interface RegisterSinkModalProps {
  isOpen: boolean;
  isRegistering: boolean;
  onRegister: (config: RegisterSinkRequest) => Promise<void>;
  onClose: () => void;
  initialConfig?: RegisterSinkRequest;
  isEditMode?: boolean;
  queueId: string;
}

type ContainerStatus = 'waiting' | 'ready' | 'error';

export const RegisterSinkModal = ({ isOpen, isRegistering, onRegister, onClose, initialConfig, isEditMode = false, queueId }: RegisterSinkModalProps) => {
  const [url, setUrl] = useState(initialConfig?.url || '');
  const [maxConcurrency, setMaxConcurrency] = useState(initialConfig?.maxConcurrency || 5);
  const [lockTtlSeconds, setLockTtlSeconds] = useState(initialConfig?.lockTtlSeconds || 30);
  const [pollingIntervalSeconds, setPollingIntervalSeconds] = useState(initialConfig?.pollingIntervalSeconds || 5);
  const [urlError, setUrlError] = useState<string | null>(null);
  const [showTestEndpoint, setShowTestEndpoint] = useState(false);
  const [dockerCommand, setDockerCommand] = useState('');
  const [copied, setCopied] = useState(false);
  const [containerStatus, setContainerStatus] = useState<ContainerStatus>('waiting');
  const [port, setPort] = useState<number>(0);
  const pollingIntervalRef = useRef<number | null>(null);

  const validateUrl = (value: string): boolean => {
    if (!value) {
      setUrlError('URL is required');
      return false;
    }

    try {
      const parsed = new URL(value);
      if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
        setUrlError('URL must use http:// or https://');
        return false;
      }
      setUrlError(null);
      return true;
    } catch {
      setUrlError('Invalid URL format');
      return false;
    }
  };

  const handleUrlChange = (value: string) => {
    setUrl(value);
    if (value) {
      validateUrl(value);
    } else {
      setUrlError('URL is required');
    }
  };

  const handleMaxConcurrencyChange = (value: number) => {
    setMaxConcurrency(Math.max(1, Math.min(100, value)));
  };

  const handleLockTtlChange = (value: number) => {
    setLockTtlSeconds(Math.max(1, Math.min(300, value)));
  };

  const handlePollingIntervalChange = (value: number) => {
    setPollingIntervalSeconds(Math.max(1, Math.min(60, value)));
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();

    if (!validateUrl(url)) {
      return;
    }

    try {
      await onRegister({
        url,
        maxConcurrency,
        lockTtlSeconds,
        pollingIntervalSeconds,
      });
      onClose();
    } catch {
      // Error handled by parent
    }
  };

  const handleBackdropClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (e.target === e.currentTarget) {
      onClose();
    }
  };

  const checkContainerHealth = async (checkPort: number): Promise<boolean> => {
    try {
      const response = await fetch(`http://localhost:${checkPort}/__admin/mappings`, {
        method: 'GET',
        signal: AbortSignal.timeout(2000),
      });
      return response.ok;
    } catch {
      return false;
    }
  };

  const applyMapping = async (targetPort: number): Promise<void> => {
    try {
      const mapping = {
        request: {
          method: 'POST',
          urlPathPattern: '.*',
        },
        response: {
          status: 200,
          headers: {
            'Content-Type': 'application/json',
          },
          body: JSON.stringify({ message: 'Stub response with status 200' }),
        },
      };

      await fetch(`http://localhost:${targetPort}/__admin/mappings`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(mapping),
      });
    } catch (err) {
      console.error('Failed to apply mapping:', err);
      throw err;
    }
  };

  const generateTestEndpoint = () => {
    const containerName = `wiremock-${queueId}`;
    // Generate random port between 8000-9999 to avoid common port conflicts
    const randomPort = Math.floor(Math.random() * 2000) + 8000;
    // Use host.docker.internal for Docker containers to reach host machine
    const dockerUrl = `http://host.docker.internal:${randomPort}`;
    const command = `docker run -d --name ${containerName} -p ${randomPort}:8080 wiremock/wiremock:latest`;

    setDockerCommand(command);
    setPort(randomPort);
    setShowTestEndpoint(true);
    setContainerStatus('waiting');

    // Auto-populate the URL field with host.docker.internal for Docker environments
    setUrl(dockerUrl);
    validateUrl(dockerUrl);
  };

  const copyToClipboard = async () => {
    try {
      await navigator.clipboard.writeText(dockerCommand);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch (err) {
      console.error('Failed to copy:', err);
    }
  };

  // Poll for container health
  useEffect(() => {
    if (!showTestEndpoint || port === 0 || containerStatus === 'ready') {
      return;
    }

    const pollContainer = async () => {
      const isHealthy = await checkContainerHealth(port);
      if (isHealthy) {
        setContainerStatus('ready');
        // Apply initial mapping (200 OK by default)
        try {
          await applyMapping(port);
        } catch (err) {
          console.error('Failed to apply initial mapping:', err);
          setContainerStatus('error');
        }
      }
    };

    // Poll every 2 seconds
    pollingIntervalRef.current = setInterval(pollContainer, 2000);

    // Immediate first check
    pollContainer();

    return () => {
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
        pollingIntervalRef.current = null;
      }
    };
  }, [showTestEndpoint, port, containerStatus]);

  // Initialize form with values when modal opens
  useEffect(() => {
    if (isOpen) {
      if (initialConfig) {
        setUrl(initialConfig.url);
        setMaxConcurrency(initialConfig.maxConcurrency);
        setLockTtlSeconds(initialConfig.lockTtlSeconds);
        setPollingIntervalSeconds(initialConfig.pollingIntervalSeconds);
        validateUrl(initialConfig.url);
      } else {
        setUrl('');
        setMaxConcurrency(5);
        setLockTtlSeconds(30);
        setPollingIntervalSeconds(5);
        setUrlError('URL is required');
      }
    }
  }, [isOpen, initialConfig]);

  // Cleanup on modal close
  useEffect(() => {
    if (!isOpen) {
      setShowTestEndpoint(false);
      setContainerStatus('waiting');
      setPort(0);
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
        pollingIntervalRef.current = null;
      }
    }
  }, [isOpen]);

  const isFormValid = url && !urlError;

  if (!isOpen) return null;

  return (
    <div className={styles.modal} onClick={handleBackdropClick}>
      <div className={styles.content}>
        <div className={styles.header}>
          <div className={styles.title}>{isEditMode ? 'Update HTTP Sink' : 'Register HTTP Sink'}</div>
          <button className={styles.close} onClick={onClose} disabled={isRegistering}>×</button>
        </div>

        <div className={styles.body}>
          <div className={styles.twoColumnLayout}>
            {/* Left Panel - Form */}
            <div className={styles.formPanel}>
              <p className={styles.description}>
                {isEditMode
                  ? 'Update the HTTP endpoint configuration for automatic message delivery.'
                  : 'Configure an HTTP endpoint to automatically receive queued messages via polling.'}
              </p>

              <form onSubmit={handleSubmit}>
            <div className={styles.formGroup}>
              <label className={styles.label}>
                URL <span className={styles.required}>*</span>
              </label>
              <input
                type="text"
                value={url}
                onChange={(e) => handleUrlChange(e.target.value)}
                className={`${styles.input} ${urlError ? styles.inputError : ''}`}
                placeholder="http://example.com/webhook"
                disabled={isRegistering}
              />
              {urlError && <div className={styles.error}>{urlError}</div>}
            </div>

            <div className={styles.formRow}>
              <div className={styles.formGroup}>
                <label className={styles.label}>
                  Max Concurrency
                  <span className={styles.hint}>(1-100)</span>
                </label>
                <input
                  type="number"
                  value={maxConcurrency}
                  onChange={(e) => handleMaxConcurrencyChange(parseInt(e.target.value) || 1)}
                  className={styles.input}
                  min={1}
                  max={100}
                  disabled={isRegistering}
                />
              </div>

              <div className={styles.formGroup}>
                <label className={styles.label}>
                  Lock TTL (seconds)
                  <span className={styles.hint}>(1-300)</span>
                </label>
                <input
                  type="number"
                  value={lockTtlSeconds}
                  onChange={(e) => handleLockTtlChange(parseInt(e.target.value) || 1)}
                  className={styles.input}
                  min={1}
                  max={300}
                  disabled={isRegistering}
                />
              </div>
            </div>

            <div className={styles.formGroup}>
              <label className={styles.label}>
                Polling Interval (seconds)
                <span className={styles.hint}>(1-60)</span>
              </label>
              <input
                type="number"
                value={pollingIntervalSeconds}
                onChange={(e) => handlePollingIntervalChange(parseInt(e.target.value) || 1)}
                className={styles.input}
                min={1}
                max={60}
                disabled={isRegistering}
              />
            </div>

                <div className={styles.footer}>
                  <button
                    type="button"
                    onClick={onClose}
                    className={styles.cancelButton}
                    disabled={isRegistering}
                  >
                    Cancel
                  </button>
                  <button
                    type="submit"
                    className={styles.registerButton}
                    disabled={!isFormValid || isRegistering}
                  >
                    {isRegistering ? (isEditMode ? 'Updating...' : 'Registering...') : (isEditMode ? 'Update' : 'Register')}
                  </button>
                </div>
              </form>
            </div>

            {/* Right Panel - Test Endpoint Helper */}
            {!isEditMode && (
              <div className={styles.helperPanel}>
                <div className={styles.helperTitle}>Need a Test Endpoint?</div>
                <p className={styles.helperDescription}>
                  I can easily help you with that! 
                </p>

              {!showTestEndpoint ? (
                <button
                  type="button"
                  onClick={generateTestEndpoint}
                  className={styles.generateButton}
                  disabled={isRegistering}
                >
                  Generate Test Endpoint
                </button>
              ) : (
                <>
                  <p className={styles.helperAccent}>
                    Just run the command in a terminal window 👇
                  </p>
                  <div className={styles.terminalWindow}>
                    <div className={styles.terminalHeader}>
                      <div className={styles.terminalDots}>
                        <span className={styles.dot}></span>
                        <span className={styles.dot}></span>
                        <span className={styles.dot}></span>
                      </div>
                      <span className={styles.terminalTitle}>Terminal</span>
                      <button
                        type="button"
                        onClick={copyToClipboard}
                        className={styles.copyButton}
                        title="Copy command"
                      >
                        {copied ? '✓' : '📋'}
                      </button>
                    </div>
                    <div className={styles.terminalBody}>
                      <div className={styles.terminalPrompt}>$</div>
                      <div className={styles.terminalCommand}>{dockerCommand}</div>
                    </div>
                    <div className={styles.terminalFooter}>
                      <p className={styles.terminalNote}>
                        ✅ URL auto-populated with <code>host.docker.internal</code> for Docker networking
                      </p>
                    </div>
                  </div>

                  {/* Container Status */}
                  <div className={styles.statusSection}>
                    <div className={styles.statusIndicator}>
                      {containerStatus === 'waiting' && (
                        <span className={styles.statusWaiting}>⏳ Waiting for container...</span>
                      )}
                      {containerStatus === 'ready' && (
                        <>
                          <span className={styles.statusReady}>✅ Container ready!</span>
                          <p className={styles.statusNote}>
                            Default stub configured (200 OK). Change response behavior in the WireMock tab after registration.
                          </p>
                        </>
                      )}
                      {containerStatus === 'error' && (
                        <span className={styles.statusError}>❌ Error configuring container</span>
                      )}
                    </div>
                  </div>
                </>
              )}
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};
