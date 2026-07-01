// Example 5: Signal Statistics
// Tracks min/max/avg for a signal over time.

// Replace with your DBC file path and signal names
const DBC_PATH = "C:/path/to/your/network.dbc";
const MESSAGE_NAME = "EngineStatus";
const SIGNAL_NAME = "RPM";

let samples = [];
let lastReportTime = 0;
const REPORT_INTERVAL_MS = 5000; // Report every 5 seconds

async function onInit() {
    console.log("Signal Statistics Example");

    // Load DBC
    const result = await dbc.load(DBC_PATH);
    if (!result.success) {
        console.error(`Failed to load DBC: ${result.error}`);
        return;
    }

    console.log(`Monitoring signal: ${MESSAGE_NAME}.${SIGNAL_NAME}`);

    // Monitor frames
    can.onFrame((frame) => {
        const decoded = dbc.decode(frame);
        if (decoded && decoded.message === MESSAGE_NAME) {
            const signal = decoded.signals[SIGNAL_NAME];
            if (signal) {
                samples.push({
                    value: signal.value,
                    timestamp: Date.now()
                });

                // Keep last 1000 samples
                if (samples.length > 1000) {
                    samples = samples.slice(-1000);
                }

                // Report periodically
                const now = Date.now();
                if (now - lastReportTime >= REPORT_INTERVAL_MS) {
                    reportStatistics();
                    lastReportTime = now;
                }
            }
        }
    });
}

function reportStatistics() {
    if (samples.length === 0) {
        console.log("No samples collected yet");
        return;
    }

    const values = samples.map(s => s.value);
    const min = Math.min(...values);
    const max = Math.max(...values);
    const avg = values.reduce((a, b) => a + b, 0) / values.length;
    const latest = values[values.length - 1];

    console.log(`\n--- ${SIGNAL_NAME} Statistics (${samples.length} samples) ---`);
    console.log(`  Current: ${latest.toFixed(2)}`);
    console.log(`  Min: ${min.toFixed(2)}`);
    console.log(`  Max: ${max.toFixed(2)}`);
    console.log(`  Average: ${avg.toFixed(2)}`);
}

function onDispose() {
    reportStatistics(); // Final report
    console.log("Signal Statistics stopped");
}
