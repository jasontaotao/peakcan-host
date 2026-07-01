// Example 1: Frame Logger
// Logs all received CAN frames with timestamp and hex data.

console.log("Frame Logger started");
console.log("Waiting for CAN frames...");

let frameCount = 0;

can.onFrame((frame) => {
    frameCount++;
    const hexData = toHex(Array.from(frame.Data));
    const fdFlag = frame.IsFd ? " [FD]" : "";
    const errFlag = frame.IsError ? " [ERR]" : "";

    console.log(`[${frameCount}] ID: ${frame.Id} | DLC: ${frame.Dlc} | Data: ${hexData}${fdFlag}${errFlag}`);
});

// Keep script running
function onDispose() {
    console.log(`Frame Logger stopped. Total frames: ${frameCount}`);
}
