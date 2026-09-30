/**
 * ATVV 协议常量。
 *
 * 规格来源：getsayall/remote-mic-app-windows（GPL-3.0），
 * 主要依据其 crates/sayall-core/src/atvv.rs 与 hardware/RC003/README.md，
 * 经本项目 AGENTS.md §4 提炼。请勿凭猜测修改字节值。
 */

const BASE_PREFIX = 'ab5e000'

/** GATT 服务与特征（128-bit 完整 UUID） */
export const BASE_UUID = `${BASE_PREFIX}0-5a21-4f05-bc7d-af01f617b664`
export const ATVV_SERVICE_UUID = `${BASE_PREFIX}1-5a21-4f05-bc7d-af01f617b664`
export const CHAR_TRANSMIT_UUID = `${BASE_PREFIX}2-5a21-4f05-bc7d-af01f617b664`
export const CHAR_AUDIO_UUID = `${BASE_PREFIX}3-5a21-4f05-bc7d-af01f617b664`
export const CHAR_CONTROL_UUID = `${BASE_PREFIX}4-5a21-4f05-bc7d-af01f617b664`

/** 主机 → 设备（写 TRANSMIT）。协议版本 >= 0x0100 时 MicOpen 不带 codec 参数。 */
export const CMD_GET_CAPS = [0x0a, 0x01, 0x00, 0x00, 0x03, 0x03]
export const micOpen = () => [0x0c, 0x00]
export const micClose = (sid) => [0x0d, (sid ?? 0) & 0xff]
export const micExtend = (sid) => [0x0e, (sid ?? 0) & 0xff]

/** 设备 → 主机（CONTROL 通知），首字节为 opcode */
export const CTRL = Object.freeze({
  STREAM_STOPPED: 0x00,
  STREAM_STARTED: 0x04,
  MIC_OPEN_REQUESTED: 0x08,
  DECODER_SYNC: 0x0a,
  CAPS: 0x0b,
})

export const CTRL_NAME = Object.freeze({
  0x00: 'StreamStopped',
  0x04: 'StreamStarted',
  0x08: 'MicOpenRequested',
  0x0a: 'DecoderSync',
  0x0b: 'Caps',
})

/** 音频参数（AGENTS.md §4.5） */
export const SAMPLE_RATE = 16000
export const DEFAULT_FRAME_SIZE = 120
/** 120 字节 = 240 samples ÷ 16 kHz = 15 ms */
export const FRAME_DURATION_MS = 15

/**
 * ⚠️ 固件免费音频窗口只有约 5.7 秒，必须在 2.5 秒处续期。
 * 早期资料里的「60 秒上限」是错的，别改这个数。
 */
export const FIRMWARE_AUDIO_WINDOW_MS = 5700
export const EXTEND_INTERVAL_MS = 2500
