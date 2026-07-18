# Fixture: AC Black Flag Resynced, wired DualSense, live gameplay (2026-07-18)

Per-channel peak meters (IAudioMeterInformation) on the controller's 4ch/48k
float render endpoint. Columns: t_seconds, ch1, ch2 (pad listening audio),
ch3, ch4 (haptic actuators). channel_meter.csv = 45 s @ ~30 Hz (light play);
channel_meter_long.csv = ~235 s @ ~40 Hz (action-heavy play).

Long-capture stats: 49 haptic bursts; durations 100 ms - 4.8 s (avg 759 ms);
peak 1.0 full scale; ch3/ch4 stereo-correlated; pad audio ~8% duty.
Game held an audio session on the endpoint (native association).
Wired HID input: report 0x01 @ ~250 Hz throughout.
