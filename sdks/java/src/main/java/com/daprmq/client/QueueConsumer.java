package com.daprmq.client;

import com.daprmq.client.types.QueueDelivery;

import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Set;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.FutureTask;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Runs a handler over a plain queue's {@link DaprMQClient#consume} stream: a handler that returns
 * acks the message, one that throws nacks or dead-letters it, and a broken stream is reopened with
 * backoff (the server has already returned whatever was unsettled on it).
 */
public final class QueueConsumer implements AutoCloseable {
    private final QueueCapableClient client;
    private final String queueId;
    private final QueueConsumerOptions options;
    private final QueueHandler handler;
    private final ConsumeOptions streamOptions;
    private final Semaphore slots; // null = unlimited
    private final AtomicBoolean stopping = new AtomicBoolean(false);
    private final CountDownLatch stopped = new CountDownLatch(1);
    private final Object startLock = new Object(); // a stop can race a handler starting
    private final Set<Future<?>> running = ConcurrentHashMap.newKeySet();
    private final Object paceLock = new Object();
    private long nextNackNanos = System.nanoTime();

    /** Test seam for the reconnect backoff and nack pacing. Defaults to a wait that stop() cuts short. */
    volatile Delay delay = millis -> stopped.await(millis, TimeUnit.MILLISECONDS);

    private volatile QueueStream current;
    private Thread runner;
    private ExecutorService handlers;

    public QueueConsumer(QueueCapableClient client, String queueId, QueueConsumerOptions options, QueueHandler handler) {
        this.client = client;
        this.queueId = queueId;
        this.options = options;
        this.handler = handler;
        boolean strict = options.getStrictOrder();
        this.streamOptions = new ConsumeOptions(
                strict ? 1 : Math.max(options.getMaxActiveMessages(), 1),
                options.getLockTtl(),
                !strict && options.getAllowCompetingConsumers(),
                null);
        int handlerLimit = strict ? 1 : options.getMaxConcurrentHandlers();
        this.slots = handlerLimit > 0 ? new Semaphore(handlerLimit) : null;
    }

    public synchronized void start() {
        if (runner != null) {
            throw new IllegalStateException("QueueConsumer already started.");
        }
        handlers = Executors.newCachedThreadPool(runnable -> {
            Thread t = new Thread(runnable, "daprmq-queue-handler-" + queueId);
            t.setDaemon(true);
            return t;
        });
        runner = new Thread(this::run, "daprmq-queue-consumer-" + queueId);
        runner.setDaemon(true);
        runner.start();
    }

    /**
     * Stops handing out messages, lets running handlers finish and settle for up to
     * drainTimeoutMillis (then interrupts them), and closes the stream, so the server returns every
     * message not yet handled straight away.
     */
    public synchronized void stop() {
        if (runner == null || !stopping.compareAndSet(false, true)) {
            return;
        }
        stopped.countDown();

        List<Future<?>> inFlight;
        synchronized (startLock) {
            inFlight = new ArrayList<>(running);
        }
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(options.getDrainTimeoutMillis());
        for (Future<?> f : inFlight) {
            try {
                f.get(Math.max(deadline - System.nanoTime(), 0), TimeUnit.NANOSECONDS);
            } catch (TimeoutException e) {
                break;
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                break;
            } catch (CancellationException | ExecutionException e) {
                // finished
            }
        }
        for (Future<?> f : inFlight) {
            f.cancel(true); // interrupts handlers still running past the drain timeout
        }
        awaitAll(inFlight);

        QueueStream stream = current;
        if (stream != null) {
            stream.close();
        }
        try {
            runner.join();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        handlers.shutdownNow();
    }

    @Override
    public void close() {
        stop();
    }

    private void run() {
        int backoffSeconds = options.getMinBackoffSeconds();

        while (!stopping.get()) {
            boolean delivered = false;
            QueueStream stream = null;
            Iterator<QueueDelivery> deliveries = null;
            try {
                stream = client.consume(queueId, streamOptions);
                current = stream;
                if (stopping.get()) {
                    stream.close();
                }
                deliveries = stream.iterator();
                while (deliveries.hasNext()) {
                    QueueDelivery delivery = deliveries.next();
                    delivered = true;
                    if (!takeSlot()) {
                        continue; // stopping: left unsettled, returned when the stream closes
                    }
                    synchronized (startLock) {
                        if (stopping.get()) {
                            if (slots != null) {
                                slots.release();
                            }
                            continue;
                        }
                        FutureTask<Void> task = new FutureTask<>(() -> handle(delivery), null) {
                            @Override
                            protected void done() {
                                running.remove(this);
                            }
                        };
                        running.add(task);
                        handlers.execute(task);
                    }
                }
            } catch (RuntimeException e) {
                // the stream broke, or was cancelled: reopen (or stop) below
            } finally {
                // Running handlers settle on this stream before it closes.
                awaitAll(new ArrayList<>(running));
                if (stream != null) {
                    stream.close();
                    awaitEnd(deliveries);
                }
                current = null;
            }

            if (stopping.get()) {
                break;
            }
            if (delivered) {
                backoffSeconds = options.getMinBackoffSeconds();
            }
            try {
                delay.sleep(backoffSeconds * 1000L);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                break;
            }
            if (stopping.get()) {
                break;
            }
            backoffSeconds = Math.min(backoffSeconds * 2, options.getMaxBackoffSeconds());
        }
    }

    /** Waits for a handler slot; false once stopping. A semaphore slot is held when it returns true. */
    private boolean takeSlot() {
        if (slots == null) {
            return !stopping.get();
        }
        try {
            while (!stopping.get()) {
                if (slots.tryAcquire(20, TimeUnit.MILLISECONDS)) {
                    return true;
                }
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        return false;
    }

    /** Runs the handler and settles the message. A settle that fails because the stream closed needs nothing more. */
    private void handle(QueueDelivery delivery) {
        QueueMessageContext context = new QueueMessageContext(queueId, delivery.lockId(), delivery.item(), delivery.priority(), delivery.deliveryCount());
        try {
            try {
                handler.handle(context);
            } catch (Exception e) {
                if (stopping.get()) {
                    return; // stopping: left unsettled, returned when the stream closes
                }
                if (options.getOnHandlerError() == QueueHandlerFailureAction.DEAD_LETTER) {
                    delivery.deadLetter().run();
                } else {
                    paceNack();
                    delivery.nack().run();
                }
                return;
            }
            delivery.ack().run();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (RuntimeException e) {
            // the stream closed under the message: the server returns it
        } finally {
            if (slots != null) {
                slots.release();
            }
        }
    }

    /** Waits for this nack's slot: at most maxRetriableErrorsPerSec nacks a second. */
    private void paceNack() throws InterruptedException {
        double rate = options.getMaxRetriableErrorsPerSec();
        if (rate <= 0) {
            return;
        }
        long waitNanos;
        synchronized (paceLock) {
            long now = System.nanoTime();
            long slot = Math.max(nextNackNanos, now);
            nextNackNanos = slot + (long) (TimeUnit.SECONDS.toNanos(1) / rate);
            waitNanos = slot - now;
        }
        if (waitNanos > 0) {
            delay.sleep(TimeUnit.NANOSECONDS.toMillis(waitNanos));
        }
    }

    private static void awaitAll(List<Future<?>> futures) {
        for (Future<?> f : futures) {
            try {
                f.get();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            } catch (CancellationException | ExecutionException e) {
                // finished
            }
        }
    }

    private static void awaitEnd(Iterator<QueueDelivery> deliveries) {
        if (deliveries == null) {
            return;
        }
        try {
            while (deliveries.hasNext()) {
                deliveries.next(); // a closed stream hands out nothing; this returns once it ends
            }
        } catch (RuntimeException e) {
            // the stream had already failed
        }
    }

    @FunctionalInterface
    interface Delay {
        void sleep(long millis) throws InterruptedException;
    }
}
