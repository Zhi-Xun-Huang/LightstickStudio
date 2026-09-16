#include <Arduino.h>
#include <Adafruit_TinyUSB.h>
#include <nrf.h>

namespace {

constexpr uint8_t kPduLength = 0x66;
constexpr uint8_t kProtocolLength = kPduLength - 6;
constexpr uint8_t kRgbProtocolLength = kProtocolLength - 1;
constexpr uint8_t kMode3Preset9 = 0x39;
constexpr uint8_t kModeArgument = 0x23;
constexpr uint8_t kPlainChecksum = kMode3Preset9 + kModeArgument;
constexpr uint8_t kSelector = 0;
// Recovered from the lightstick firmware's PhyPlusPhy initialization:
// pktFmt=4 is BLE Long Range S=8 and rfChn=78 is 2478 MHz. nRF52840
// cannot represent the PHY6222 whitening seed 0x38 in DATAWHITEIV, so
// profile 4 generates the PDU, CRC and whitening stream in software.
constexpr uint8_t kChannelCount = 1;
constexpr uint8_t kFrequencies[kChannelCount] = {78};
constexpr uint8_t kBurstsPerCandidate = 3;
constexpr uint16_t kInterPacketDelayUs = 700;
constexpr uint16_t kCandidateDwellMs = 45;
constexpr uint8_t kSoftwareWhiteningProfile = 4;
constexpr uint8_t kPhyPlusWhiteningSeed = 0x38;
constexpr uint8_t kRecoveredXorKey = 0x13;
constexpr char kControllerIdentity[] = "JFKJ_LIGHTSTICK_TX_V1";
constexpr uint16_t kReliableTransmitMs = 1000;
constexpr uint16_t kAnimationFrameMs = 40;
constexpr uint8_t kAnimationBursts = 3;
constexpr size_t kLogicalPacketLength = sizeof(uint8_t) * (2 + kPduLength);
constexpr size_t kAirPacketLength = kLogicalPacketLength + 3;

alignas(4) uint8_t txPacket[2 + kPduLength];
alignas(4) uint8_t airPacket[kAirPacketLength];
alignas(4) uint8_t rxPacket[258];
String commandLine;
bool radioReady = false;
uint8_t radioProfile = kSoftwareWhiteningProfile;
uint32_t lastRgbSaltNonce = 0;

uint8_t selectedWhiteningIv() {
  return (radioProfile & 1U) ? 0x38 : 14;
}

uint32_t selectedRadioMode() {
  return (radioProfile & 2U) ? RADIO_MODE_MODE_Ble_LR500Kbit
                             : RADIO_MODE_MODE_Ble_LR125Kbit;
}

const char *selectedRateName() {
  if (radioProfile == kSoftwareWhiteningProfile) {
    return "LR125K/S=8 software CRC+whitening";
  }
  return (radioProfile & 2U) ? "LR500K/S=2" : "LR125K/S=8";
}

void printInfo() {
  Serial.printf("FICR PART=0x%08lX VARIANT=0x%08lX\n",
                static_cast<unsigned long>(NRF_FICR->INFO.PART),
                static_cast<unsigned long>(NRF_FICR->INFO.VARIANT));
  Serial.printf("DEVICEID=%08lX%08lX\n",
                static_cast<unsigned long>(NRF_FICR->DEVICEID[1]),
                static_cast<unsigned long>(NRF_FICR->DEVICEID[0]));
  Serial.printf("UICR NFCPINS=0x%08lX (read only)\n",
                static_cast<unsigned long>(NRF_UICR->NFCPINS));
  Serial.printf("RADIO ready=%s state=%lu frequency=%lu whitening_iv=%lu\n",
                radioReady ? "yes" : "no",
                static_cast<unsigned long>(NRF_RADIO->STATE),
                static_cast<unsigned long>(NRF_RADIO->FREQUENCY),
                static_cast<unsigned long>(NRF_RADIO->DATAWHITEIV));
  if (radioProfile == kSoftwareWhiteningProfile) {
    Serial.printf("RF profile=%u BLE %s, 2478 MHz, PHY6222 seed=0x%02X\n",
                  radioProfile, selectedRateName(), kPhyPlusWhiteningSeed);
  } else {
    Serial.printf("RF profile=%u BLE %s, 2478 MHz, whitening IV=%u\n",
                  radioProfile, selectedRateName(), selectedWhiteningIv());
  }
}

bool waitForEvent(volatile uint32_t &eventRegister, uint32_t timeoutUs) {
  const uint32_t startedAt = micros();
  while (eventRegister == 0) {
    if (micros() - startedAt >= timeoutUs) return false;
  }
  return true;
}

void configureCommonPacketFields() {
  NRF_RADIO->PCNF1 =
      (255UL << RADIO_PCNF1_MAXLEN_Pos) |
      (0UL << RADIO_PCNF1_STATLEN_Pos) |
      (3UL << RADIO_PCNF1_BALEN_Pos) |
      (RADIO_PCNF1_ENDIAN_Little << RADIO_PCNF1_ENDIAN_Pos) |
      (RADIO_PCNF1_WHITEEN_Enabled << RADIO_PCNF1_WHITEEN_Pos);
  NRF_RADIO->PREFIX0 = 0x8E;
  NRF_RADIO->BASE0 = 0x89BED600;
  NRF_RADIO->TXADDRESS = 0;
  NRF_RADIO->RXADDRESSES = 1;
  NRF_RADIO->CRCCNF =
      (RADIO_CRCCNF_LEN_Three << RADIO_CRCCNF_LEN_Pos) |
      (RADIO_CRCCNF_SKIPADDR_Skip << RADIO_CRCCNF_SKIPADDR_Pos);
  NRF_RADIO->CRCINIT = 0x555555;
  NRF_RADIO->CRCPOLY = 0x00065B;
}

bool initializeRadio() {
  NRF_CLOCK->EVENTS_HFCLKSTARTED = 0;
  NRF_CLOCK->TASKS_HFCLKSTART = 1;
  if (!waitForEvent(NRF_CLOCK->EVENTS_HFCLKSTARTED, 100000)) {
    Serial.println("ERROR: HFCLK did not start");
    return false;
  }

  NRF_RADIO->POWER = 1;
  NRF_RADIO->TASKS_DISABLE = 1;
  delayMicroseconds(10);
  NRF_RADIO->TXPOWER =
      RADIO_TXPOWER_TXPOWER_0dBm << RADIO_TXPOWER_TXPOWER_Pos;
  NRF_RADIO->MODE = selectedRadioMode() << RADIO_MODE_MODE_Pos;
  if (radioProfile == kSoftwareWhiteningProfile) {
    // Treat the complete pre-whitened PDU+CRC as fixed-length payload. The
    // RADIO still emits the coded-PHY preamble, access address, CI, TERM1,
    // FEC and TERM2 fields, but does not alter our 107 data bytes.
    NRF_RADIO->PCNF0 =
        (0UL << RADIO_PCNF0_S0LEN_Pos) |
        (0UL << RADIO_PCNF0_LFLEN_Pos) |
        (RADIO_PCNF0_PLEN_LongRange << RADIO_PCNF0_PLEN_Pos) |
        (2UL << RADIO_PCNF0_CILEN_Pos) |
        (3UL << RADIO_PCNF0_TERMLEN_Pos);
    NRF_RADIO->PCNF1 =
        (kAirPacketLength << RADIO_PCNF1_MAXLEN_Pos) |
        (kAirPacketLength << RADIO_PCNF1_STATLEN_Pos) |
        (3UL << RADIO_PCNF1_BALEN_Pos) |
        (RADIO_PCNF1_ENDIAN_Little << RADIO_PCNF1_ENDIAN_Pos) |
        (RADIO_PCNF1_WHITEEN_Disabled << RADIO_PCNF1_WHITEEN_Pos);
    NRF_RADIO->PREFIX0 = 0x8E;
    NRF_RADIO->BASE0 = 0x89BED600;
    NRF_RADIO->TXADDRESS = 0;
    NRF_RADIO->RXADDRESSES = 1;
    NRF_RADIO->CRCCNF = RADIO_CRCCNF_LEN_Disabled << RADIO_CRCCNF_LEN_Pos;
    NRF_RADIO->CRCINIT = 0;
    NRF_RADIO->CRCPOLY = 0;
  } else {
    NRF_RADIO->PCNF0 =
        (1UL << RADIO_PCNF0_S0LEN_Pos) |
        (8UL << RADIO_PCNF0_LFLEN_Pos) |
        (RADIO_PCNF0_PLEN_LongRange << RADIO_PCNF0_PLEN_Pos) |
        (2UL << RADIO_PCNF0_CILEN_Pos) |
        (3UL << RADIO_PCNF0_TERMLEN_Pos);
    configureCommonPacketFields();
  }
  NRF_RADIO->TIFS = 150;
  NRF_RADIO->PACKETPTR = reinterpret_cast<uint32_t>(
      radioProfile == kSoftwareWhiteningProfile ? airPacket : txPacket);
  // On BLE coded PHY, END occurs before the final coded termination field has
  // left the antenna. Disabling there truncates every packet. PHYEND is the
  // actual end-of-air event for LR125K/LR500K.
  NRF_RADIO->SHORTS = RADIO_SHORTS_READY_START_Msk |
                      RADIO_SHORTS_PHYEND_DISABLE_Msk;

  radioReady = true;
  return true;
}

void buildCandidatePacket(uint8_t candidate, bool directWhite) {
  txPacket[0] = 0xA5;
  txPacket[1] = kPduLength;
  for (uint8_t i = 0; i < 6; ++i) txPacket[2 + i] = 0x11;

  uint8_t *protocol = &txPacket[8];
  if (directWhite) {
    // Mode 1 indexes three adjacent bytes using the lightstick's local
    // group/seat offset. Fill every possible slot so every group reads white.
    protocol[0] = 0x10;
    for (uint8_t i = 1; i < kProtocolLength - 2; ++i) {
      protocol[i] = 0xFF;
    }
  } else {
    protocol[0] = kMode3Preset9;
    protocol[1] = kModeArgument;
    for (uint8_t i = 2; i < kProtocolLength - 2; ++i) {
      protocol[i] = 0;
    }
  }

  uint8_t checksum = 0;
  for (uint8_t i = 0; i < kProtocolLength - 2; ++i) {
    checksum = static_cast<uint8_t>(checksum + protocol[i]);
  }
  protocol[kProtocolLength - 2] = checksum;
  protocol[kProtocolLength - 1] = kSelector;

  // The receiver selects one byte from its 64-byte table using the final
  // selector, then XORs bytes 1 through length-2 with that value.
  for (uint8_t i = 1; i < kProtocolLength - 1; ++i) {
    protocol[i] ^= candidate;
  }
}

bool hasUniqueXorChecksum(const uint8_t *protocol) {
  for (uint16_t delta = 1; delta <= 255; ++delta) {
    uint8_t decodedSum = protocol[0];
    for (uint8_t i = 1; i < kRgbProtocolLength - 2; ++i) {
      decodedSum = static_cast<uint8_t>(decodedSum +
          (protocol[i] ^ static_cast<uint8_t>(delta)));
    }
    const uint8_t decodedChecksum = static_cast<uint8_t>(
        protocol[kRgbProtocolLength - 2] ^ static_cast<uint8_t>(delta));
    if (decodedSum == decodedChecksum) return false;
  }
  return true;
}

bool buildRgbCandidatePacket(uint8_t candidate, uint8_t red, uint8_t green,
                             uint8_t blue) {
  txPacket[0] = 0xA5;
  txPacket[1] = 6 + kRgbProtocolLength;
  for (uint8_t i = 0; i < 6; ++i) txPacket[2 + i] = 0x11;

  uint8_t *protocol = &txPacket[8];
  // The checksum is only eight bits, so a highly repetitive RGB payload can
  // accidentally validate after decryption with several wrong XOR keys. Salt
  // unused bytes and search for a payload for which delta=0 is the sole key
  // that satisfies the receiver's additive checksum.
  bool found = false;
  // An odd protocol length avoids the unavoidable delta=0x80 collision of
  // the original even-length packet. Bound the search even if assumptions fail.
  for (uint32_t nonce = 0; nonce < 4096; ++nonce) {
    memset(protocol, 0, kProtocolLength);
    protocol[0] = 0x10;

    // Firmware loads a stick number N in the range 1..20 and reads RGB from
    // offsets (3*N-1), (3*N), (3*N+1). Populate all group slots identically.
    for (uint8_t group = 1; group <= 20; ++group) {
      const uint8_t offset = static_cast<uint8_t>(3 * group - 1);
      protocol[offset] = red;
      protocol[offset + 1] = green;
      protocol[offset + 2] = blue;
    }

    uint32_t randomState = 0xA5F1523DUL ^ (nonce * 0x9E3779B9UL) ^
                           (static_cast<uint32_t>(red) << 16) ^
                           (static_cast<uint32_t>(green) << 8) ^ blue;
    auto nextSaltByte = [&randomState]() {
      randomState ^= randomState << 13;
      randomState ^= randomState >> 17;
      randomState ^= randomState << 5;
      return static_cast<uint8_t>(randomState);
    };
    protocol[1] = nextSaltByte();
    for (uint8_t i = 62; i < kRgbProtocolLength - 2; ++i) {
      protocol[i] = nextSaltByte();
    }

    uint8_t checksum = 0;
    for (uint8_t i = 0; i < kRgbProtocolLength - 2; ++i) {
      checksum = static_cast<uint8_t>(checksum + protocol[i]);
    }
    protocol[kRgbProtocolLength - 2] = checksum;
    protocol[kRgbProtocolLength - 1] = kSelector;
    if (hasUniqueXorChecksum(protocol)) {
      lastRgbSaltNonce = nonce;
      found = true;
      break;
    }
  }

  if (!found) {
    Serial.println("ERROR: no unique checksum payload found; no transmission");
    return false;
  }
  for (uint8_t i = 1; i < kRgbProtocolLength - 1; ++i) {
    protocol[i] ^= candidate;
  }
  return true;
}

void buildEffectPacket(uint8_t mode, uint8_t preset, uint8_t argument) {
  txPacket[0] = 0xA5;
  txPacket[1] = kPduLength;
  for (uint8_t i = 0; i < 6; ++i) txPacket[2 + i] = 0x11;

  uint8_t *protocol = &txPacket[8];
  memset(protocol, 0, kProtocolLength);
  protocol[0] = static_cast<uint8_t>(((mode & 0x0F) << 4) |
                                     (preset & 0x0F));
  protocol[1] = argument;

  uint8_t checksum = 0;
  for (uint8_t i = 0; i < kProtocolLength - 2; ++i) {
    checksum = static_cast<uint8_t>(checksum + protocol[i]);
  }
  protocol[kProtocolLength - 2] = checksum;
  protocol[kProtocolLength - 1] = kSelector;
  for (uint8_t i = 1; i < kProtocolLength - 1; ++i) {
    protocol[i] ^= kRecoveredXorKey;
  }
}

uint8_t reverseBits(uint8_t value) {
  value = static_cast<uint8_t>((value >> 4) | (value << 4));
  value = static_cast<uint8_t>(((value & 0xCC) >> 2) |
                               ((value & 0x33) << 2));
  return static_cast<uint8_t>(((value & 0xAA) >> 1) |
                              ((value & 0x55) << 1));
}

uint32_t calculateBleCrc(const uint8_t *data, size_t length) {
  // BLE feeds each PDU byte least-significant bit first. This register form
  // shifts toward bit 23 using x^24+x^10+x^9+x^6+x^4+x^3+x+1.
  uint32_t crc = 0x555555;
  for (size_t byteIndex = 0; byteIndex < length; ++byteIndex) {
    uint8_t value = data[byteIndex];
    for (uint8_t bit = 0; bit < 8; ++bit) {
      const bool feedback = (((crc >> 23) & 1U) ^ (value & 1U)) != 0;
      crc = (crc << 1) & 0xFFFFFF;
      if (feedback) crc ^= 0x00065B;
      value >>= 1;
    }
  }
  return crc;
}

uint8_t nextPhyPlusWhiteningBit(uint8_t &state) {
  // PHY6222 stores the seven whitening-LFSR stages in the reverse bit order
  // used by Nordic. Bit 6 is emitted, then x^7+x^4+1 advances the state.
  const uint8_t output = static_cast<uint8_t>((state >> 6) & 1U);
  state = static_cast<uint8_t>((state << 1) & 0x7F);
  if (output) state ^= 0x11;
  return output;
}

uint32_t buildSoftwareAirPacket() {
  const size_t logicalLength = 2 + txPacket[1];
  const size_t airLength = logicalLength + 3;
  const uint32_t crc = calculateBleCrc(txPacket, logicalLength);
  uint8_t unwhitened[kAirPacketLength];
  memcpy(unwhitened, txPacket, logicalLength);

  // The CRC register is sent c23 first. Reverse each register byte so Nordic's
  // little-endian raw payload serializer puts that bit first on air.
  unwhitened[logicalLength] =
      reverseBits(static_cast<uint8_t>((crc >> 16) & 0xFF));
  unwhitened[logicalLength + 1] =
      reverseBits(static_cast<uint8_t>((crc >> 8) & 0xFF));
  unwhitened[logicalLength + 2] =
      reverseBits(static_cast<uint8_t>(crc & 0xFF));

  uint8_t whiteningState = kPhyPlusWhiteningSeed;
  for (size_t byteIndex = 0; byteIndex < airLength; ++byteIndex) {
    uint8_t output = 0;
    for (uint8_t bit = 0; bit < 8; ++bit) {
      const uint8_t inputBit =
          static_cast<uint8_t>((unwhitened[byteIndex] >> bit) & 1U);
      output |= static_cast<uint8_t>(
          (inputBit ^ nextPhyPlusWhiteningBit(whiteningState)) << bit);
    }
    airPacket[byteIndex] = output;
  }
  return crc;
}

bool transmitPacketOnChannel(uint8_t channelIndex) {
  NRF_RADIO->FREQUENCY = kFrequencies[channelIndex];
  if (radioProfile != kSoftwareWhiteningProfile) {
    NRF_RADIO->DATAWHITEIV = selectedWhiteningIv();
  } else {
    const uint32_t airLength = 2 + txPacket[1] + 3;
    NRF_RADIO->PCNF1 = (NRF_RADIO->PCNF1 & ~RADIO_PCNF1_STATLEN_Msk) |
                      (airLength << RADIO_PCNF1_STATLEN_Pos);
  }
  NRF_RADIO->EVENTS_READY = 0;
  NRF_RADIO->EVENTS_END = 0;
  NRF_RADIO->EVENTS_DISABLED = 0;
  NRF_RADIO->PACKETPTR = reinterpret_cast<uint32_t>(
      radioProfile == kSoftwareWhiteningProfile ? airPacket : txPacket);
  __DSB();
  NRF_RADIO->TASKS_TXEN = 1;
  if (!waitForEvent(NRF_RADIO->EVENTS_DISABLED, 10000)) {
    NRF_RADIO->TASKS_DISABLE = 1;
    Serial.printf("ERROR: RADIO timeout on channel %u\n",
                  selectedWhiteningIv());
    return false;
  }
  return true;
}

bool sendCandidate(uint8_t candidate, bool directWhite = false) {
  if (!radioReady && !initializeRadio()) return false;
  buildCandidatePacket(candidate, directWhite);
  if (radioProfile == kSoftwareWhiteningProfile) {
    const uint32_t crc = buildSoftwareAirPacket();
    Serial.printf("  software CRC=%06lX air[0..7]=",
                  static_cast<unsigned long>(crc));
    for (uint8_t i = 0; i < 8; ++i) Serial.printf("%02X", airPacket[i]);
    Serial.println();
  }
  for (uint8_t burst = 0; burst < kBurstsPerCandidate; ++burst) {
    for (uint8_t channel = 0; channel < kChannelCount; ++channel) {
      if (!transmitPacketOnChannel(channel)) return false;
      delayMicroseconds(kInterPacketDelayUs);
    }
  }
  return true;
}

bool sendRgbCandidate(uint8_t candidate, uint8_t red, uint8_t green,
                      uint8_t blue) {
  if (!radioReady && !initializeRadio()) return false;
  if (!buildRgbCandidatePacket(candidate, red, green, blue)) return false;
  Serial.printf("  collision-free salt nonce=%lu\n",
                static_cast<unsigned long>(lastRgbSaltNonce));
  if (radioProfile == kSoftwareWhiteningProfile) {
    const uint32_t crc = buildSoftwareAirPacket();
    Serial.printf("  software CRC=%06lX air[0..7]=",
                  static_cast<unsigned long>(crc));
    for (uint8_t i = 0; i < 8; ++i) Serial.printf("%02X", airPacket[i]);
    Serial.println();
  }
  for (uint8_t burst = 0; burst < kBurstsPerCandidate; ++burst) {
    for (uint8_t channel = 0; channel < kChannelCount; ++channel) {
      if (!transmitPacketOnChannel(channel)) return false;
      delayMicroseconds(kInterPacketDelayUs);
    }
  }
  return true;
}

bool sendRgbFrame(uint8_t red, uint8_t green, uint8_t blue) {
  if (!radioReady && !initializeRadio()) return false;
  if (!buildRgbCandidatePacket(kRecoveredXorKey, red, green, blue)) {
    return false;
  }
  if (radioProfile == kSoftwareWhiteningProfile) buildSoftwareAirPacket();
  for (uint8_t burst = 0; burst < kAnimationBursts; ++burst) {
    for (uint8_t channel = 0; channel < kChannelCount; ++channel) {
      if (!transmitPacketOnChannel(channel)) return false;
      delayMicroseconds(kInterPacketDelayUs);
    }
  }
  return true;
}

void colorWheel(uint16_t position, uint8_t &red, uint8_t &green,
                uint8_t &blue) {
  position %= 768;
  const uint8_t offset = static_cast<uint8_t>(position & 0xFF);
  if (position < 256) {
    red = static_cast<uint8_t>(255 - offset);
    green = offset;
    blue = 0;
  } else if (position < 512) {
    red = 0;
    green = static_cast<uint8_t>(255 - offset);
    blue = offset;
  } else {
    red = offset;
    green = 0;
    blue = static_cast<uint8_t>(255 - offset);
  }
}

bool runRainbow(uint8_t seconds) {
  const uint32_t frameCount =
      (static_cast<uint32_t>(seconds) * 1000U) / kAnimationFrameMs;
  uint32_t nextFrameAt = millis();
  Serial.printf("RAINBOW begin: %u seconds, %lu frames\n", seconds,
                static_cast<unsigned long>(frameCount));
  for (uint32_t frame = 0; frame < frameCount; ++frame) {
    uint8_t red;
    uint8_t green;
    uint8_t blue;
    colorWheel(static_cast<uint16_t>((frame * 768U) / frameCount),
               red, green, blue);
    if (!sendRgbFrame(red, green, blue)) return false;
    nextFrameAt += kAnimationFrameMs;
    while (static_cast<int32_t>(nextFrameAt - millis()) > 0) yield();
  }
  Serial.println("RAINBOW complete");
  return true;
}

bool runBreathe(uint8_t red, uint8_t green, uint8_t blue, uint8_t cycles) {
  constexpr uint8_t kFramesPerCycle = 50;
  const uint32_t frameCount =
      static_cast<uint32_t>(cycles) * kFramesPerCycle;
  uint32_t nextFrameAt = millis();
  Serial.printf("BREATHE begin: rgb=(%u,%u,%u), cycles=%u\n", red, green,
                blue, cycles);
  for (uint32_t frame = 0; frame < frameCount; ++frame) {
    const uint8_t phase = static_cast<uint8_t>(frame % kFramesPerCycle);
    const uint16_t level = phase < 25
        ? static_cast<uint16_t>(phase) * 255U / 24U
        : static_cast<uint16_t>(49U - phase) * 255U / 24U;
    const uint8_t frameRed =
        static_cast<uint8_t>((static_cast<uint16_t>(red) * level) / 255U);
    const uint8_t frameGreen =
        static_cast<uint8_t>((static_cast<uint16_t>(green) * level) / 255U);
    const uint8_t frameBlue =
        static_cast<uint8_t>((static_cast<uint16_t>(blue) * level) / 255U);
    if (!sendRgbFrame(frameRed, frameGreen, frameBlue)) return false;
    nextFrameAt += kAnimationFrameMs;
    while (static_cast<int32_t>(nextFrameAt - millis()) > 0) yield();
  }
  if (!sendRgbFrame(0, 0, 0)) return false;
  Serial.println("BREATHE complete");
  return true;
}

bool sendRgbReliable(uint8_t red, uint8_t green, uint8_t blue) {
  if (!radioReady && !initializeRadio()) return false;
  if (!buildRgbCandidatePacket(kRecoveredXorKey, red, green, blue)) {
    return false;
  }
  const uint32_t crc = buildSoftwareAirPacket();
  Serial.printf("TX reliable rgb=(%u,%u,%u), key=0x%02X, CRC=%06lX\n",
                red, green, blue, kRecoveredXorKey,
                static_cast<unsigned long>(crc));

  uint32_t packetCount = 0;
  const uint32_t startedAt = millis();
  do {
    for (uint8_t channel = 0; channel < kChannelCount; ++channel) {
      if (!transmitPacketOnChannel(channel)) return false;
      ++packetCount;
      delayMicroseconds(kInterPacketDelayUs);
    }
    yield();
  } while (millis() - startedAt < kReliableTransmitMs);

  Serial.printf("TX complete: %lu packets over %u ms\n",
                static_cast<unsigned long>(packetCount),
                kReliableTransmitMs);
  return true;
}

bool sendEffectReliable(uint8_t mode, uint8_t preset, uint8_t argument) {
  if (!radioReady && !initializeRadio()) return false;
  buildEffectPacket(mode, preset, argument);
  const uint32_t crc = buildSoftwareAirPacket();
  Serial.printf("TX reliable mode=%u preset=%u argument=%u, key=0x%02X, "
                "CRC=%06lX\n",
                mode, preset, argument, kRecoveredXorKey,
                static_cast<unsigned long>(crc));

  uint32_t packetCount = 0;
  const uint32_t startedAt = millis();
  do {
    for (uint8_t channel = 0; channel < kChannelCount; ++channel) {
      if (!transmitPacketOnChannel(channel)) return false;
      ++packetCount;
      delayMicroseconds(kInterPacketDelayUs);
    }
    yield();
  } while (millis() - startedAt < kReliableTransmitMs);

  Serial.printf("TX complete: %lu packets over %u ms\n",
                static_cast<unsigned long>(packetCount),
                kReliableTransmitMs);
  return true;
}

bool packetHasLightstickAddress(uint8_t payloadLength) {
  static constexpr uint8_t kTargetAdvA[6] = {
      0xFC, 0x1E, 0xCF, 0x1A, 0xFF, 0xFF};
  if (payloadLength < 6) return false;
  for (uint8_t i = 0; i < 6; ++i) {
    if (rxPacket[2 + i] != kTargetAdvA[i]) return false;
  }
  return true;
}

void sniffBleAdvertisements() {
  static constexpr uint8_t kAdvFrequencies[3] = {2, 26, 80};
  static constexpr uint8_t kAdvWhiteningIv[3] = {37, 38, 39};

  NRF_RADIO->TASKS_DISABLE = 1;
  NRF_RADIO->POWER = 0;
  delay(2);
  NRF_RADIO->POWER = 1;
  NRF_RADIO->MODE = RADIO_MODE_MODE_Ble_1Mbit << RADIO_MODE_MODE_Pos;
  NRF_RADIO->PCNF0 =
      (1UL << RADIO_PCNF0_S0LEN_Pos) |
      (8UL << RADIO_PCNF0_LFLEN_Pos) |
      (RADIO_PCNF0_PLEN_8bit << RADIO_PCNF0_PLEN_Pos);
  configureCommonPacketFields();
  NRF_RADIO->PACKETPTR = reinterpret_cast<uint32_t>(rxPacket);
  NRF_RADIO->SHORTS = RADIO_SHORTS_READY_START_Msk |
                      RADIO_SHORTS_END_DISABLE_Msk;

  uint32_t crcOkCount = 0;
  uint32_t targetCount = 0;
  const uint32_t scanStartedAt = millis();
  uint8_t channel = 0;
  Serial.println("BLE sniff started: 12 seconds on channels 37/38/39");

  while (millis() - scanStartedAt < 12000) {
    memset(rxPacket, 0, sizeof(rxPacket));
    NRF_RADIO->FREQUENCY = kAdvFrequencies[channel];
    NRF_RADIO->DATAWHITEIV = kAdvWhiteningIv[channel];
    NRF_RADIO->EVENTS_END = 0;
    NRF_RADIO->EVENTS_DISABLED = 0;
    NRF_RADIO->EVENTS_CRCOK = 0;
    NRF_RADIO->PACKETPTR = reinterpret_cast<uint32_t>(rxPacket);
    __DSB();
    NRF_RADIO->TASKS_RXEN = 1;

    const uint32_t windowStartedAt = millis();
    while (NRF_RADIO->EVENTS_END == 0 &&
           millis() - windowStartedAt < 120) {
      yield();
    }

    if (NRF_RADIO->EVENTS_END != 0) {
      while (NRF_RADIO->EVENTS_DISABLED == 0) yield();
      if (NRF_RADIO->CRCSTATUS != 0) {
        ++crcOkCount;
        const uint8_t length = rxPacket[1];
        if (packetHasLightstickAddress(length)) {
          ++targetCount;
          Serial.printf("TARGET seen on ch%u, PDU type=0x%02X len=%u\n",
                        37 + channel, rxPacket[0], length);
        }
      }
    } else {
      NRF_RADIO->TASKS_DISABLE = 1;
      waitForEvent(NRF_RADIO->EVENTS_DISABLED, 2000);
    }
    channel = static_cast<uint8_t>((channel + 1) % 3);
  }

  NRF_RADIO->TASKS_DISABLE = 1;
  NRF_RADIO->POWER = 0;
  radioReady = false;
  Serial.printf("BLE sniff complete: CRC-ok=%lu target=%lu\n",
                static_cast<unsigned long>(crcOkCount),
                static_cast<unsigned long>(targetCount));
}

void printHelp() {
  Serial.println("Commands:");
  Serial.println("  identify         - machine-readable controller identity");
  Serial.println("  color R G B      - reliable factory-control RGB using recovered key");
  Serial.println("  frame R G B      - low-latency RGB frame for a PC controller");
  Serial.println("  off              - factory-control off; button remains locked");
  Serial.println("  rainbow S        - stream a smooth RGB cycle for 1..30 seconds");
  Serial.println("  breathe R G B C  - breathe an RGB color for 1..20 cycles");
  Serial.println("  effect P A       - Mode 3 built-in fade; preset 0..15, argument 0..255");
  Serial.println("  mode M P A       - research any mode/preset/argument (M/P 0..15)");
  Serial.println("  info             - show chip and RADIO state");
  Serial.println("  sendkey N        - transmit one candidate (0..255)");
  Serial.println("  range A B        - transmit each candidate A..B (0..255)");
  Serial.println("  sendwhite N      - mode 1, force every group to white");
  Serial.println("  sendrgb N R G B  - mode 1 RGB for every group (0..255)");
  Serial.println("  probe A B        - split key range into red/green/blue/purple quarters");
  Serial.println("  rangewhite A B   - scan mode 1 white candidates");
  Serial.println("  profile N        - RF profile 0..4; 4=software CRC+seed 0x38");
  Serial.println("  sniffble         - verify raw RADIO RX against the lightstick BLE address");
  Serial.println("  stop             - disable RADIO");
  Serial.println("After battery insertion, press the lightstick button once before color/off.");
  Serial.println("No RF packet is transmitted automatically.");
}

void handleCommand(String line) {
  line.trim();
  if (line == "help") {
    printHelp();
    return;
  }
  if (line == "identify") {
    Serial.print("ID ");
    Serial.println(kControllerIdentity);
    return;
  }
  if (line == "info") {
    printInfo();
    return;
  }
  if (line == "stop") {
    NRF_RADIO->TASKS_DISABLE = 1;
    NRF_RADIO->POWER = 0;
    radioReady = false;
    Serial.println("RADIO stopped");
    return;
  }
  if (line == "sniffble") {
    sniffBleAdvertisements();
    return;
  }

  if (line == "off") {
    const bool ok = sendRgbReliable(0, 0, 0);
    Serial.println(ok ? "Lightstick commanded off (factory lock retained)"
                      : "TX failed");
    return;
  }

  int animationSeconds = -1;
  if (line.startsWith("rainbow ") &&
      sscanf(line.c_str(), "rainbow %d", &animationSeconds) == 1) {
    if (animationSeconds < 1 || animationSeconds > 30) {
      Serial.println("ERROR: rainbow duration must be 1..30 seconds");
      return;
    }
    const bool ok = runRainbow(static_cast<uint8_t>(animationSeconds));
    Serial.println(ok ? "Rainbow command complete" : "Rainbow failed");
    return;
  }

  int red = -1;
  int green = -1;
  int blue = -1;
  int cycles = -1;
  if (line.startsWith("breathe ") &&
      sscanf(line.c_str(), "breathe %d %d %d %d", &red, &green, &blue,
             &cycles) == 4) {
    if (red < 0 || red > 255 || green < 0 || green > 255 || blue < 0 ||
        blue > 255 || cycles < 1 || cycles > 20) {
      Serial.println("ERROR: RGB must be 0..255 and cycles must be 1..20");
      return;
    }
    const bool ok = runBreathe(static_cast<uint8_t>(red),
                               static_cast<uint8_t>(green),
                               static_cast<uint8_t>(blue),
                               static_cast<uint8_t>(cycles));
    Serial.println(ok ? "Breathe command complete" : "Breathe failed");
    return;
  }

  if (line.startsWith("frame ") &&
      sscanf(line.c_str(), "frame %d %d %d", &red, &green, &blue) == 3) {
    if (red < 0 || red > 255 || green < 0 || green > 255 || blue < 0 ||
        blue > 255) {
      Serial.println("ERROR: frame RGB must each be 0..255");
      return;
    }
    if (!sendRgbFrame(static_cast<uint8_t>(red),
                      static_cast<uint8_t>(green),
                      static_cast<uint8_t>(blue))) {
      Serial.println("ERROR: frame transmission failed");
    }
    return;
  }

  if (line.startsWith("color ") &&
      sscanf(line.c_str(), "color %d %d %d", &red, &green, &blue) == 3) {
    if (red < 0 || red > 255 || green < 0 || green > 255 || blue < 0 ||
        blue > 255) {
      Serial.println("ERROR: R, G and B must each be 0..255");
      return;
    }
    const bool ok = sendRgbReliable(static_cast<uint8_t>(red),
                                    static_cast<uint8_t>(green),
                                    static_cast<uint8_t>(blue));
    Serial.println(ok ? "Factory color command complete" : "TX failed");
    return;
  }

  int effectPreset = -1;
  int effectArgument = -1;
  if (line.startsWith("effect ") &&
      sscanf(line.c_str(), "effect %d %d", &effectPreset,
             &effectArgument) == 2) {
    if (effectPreset < 0 || effectPreset > 15 || effectArgument < 0 ||
        effectArgument > 255) {
      Serial.println("ERROR: preset must be 0..15 and argument must be 0..255");
      return;
    }
    const bool ok = sendEffectReliable(3, static_cast<uint8_t>(effectPreset),
                                       static_cast<uint8_t>(effectArgument));
    Serial.println(ok ? "Factory effect command complete" : "TX failed");
    return;
  }

  int effectMode = -1;
  if (line.startsWith("mode ") &&
      sscanf(line.c_str(), "mode %d %d %d", &effectMode, &effectPreset,
             &effectArgument) == 3) {
    if (effectMode < 0 || effectMode > 15 || effectPreset < 0 ||
        effectPreset > 15 || effectArgument < 0 || effectArgument > 255) {
      Serial.println("ERROR: mode/preset must be 0..15; argument 0..255");
      return;
    }
    const bool ok = sendEffectReliable(static_cast<uint8_t>(effectMode),
                                       static_cast<uint8_t>(effectPreset),
                                       static_cast<uint8_t>(effectArgument));
    Serial.println(ok ? "Factory mode command complete" : "TX failed");
    return;
  }

  int selectedProfile = -1;
  if (line.startsWith("profile ") &&
      sscanf(line.c_str(), "profile %d", &selectedProfile) == 1) {
    if (selectedProfile < 0 || selectedProfile > 4) {
      Serial.println("ERROR: profile must be 0..4");
      return;
    }
    if (radioReady) {
      NRF_RADIO->TASKS_DISABLE = 1;
      NRF_RADIO->POWER = 0;
      radioReady = false;
    }
    radioProfile = static_cast<uint8_t>(selectedProfile);
    printInfo();
    return;
  }

  int first = -1;
  int last = -1;
  if (line.startsWith("sendkey ") &&
      sscanf(line.c_str(), "sendkey %d", &first) == 1) {
    if (first < 0 || first > 255) {
      Serial.println("ERROR: candidate must be 0..255");
      return;
    }
    Serial.printf("TX candidate=%d\n", first);
    const bool ok = sendCandidate(static_cast<uint8_t>(first));
    Serial.println(ok ? "TX complete" : "TX failed");
    return;
  }

  if (line.startsWith("sendwhite ") &&
      sscanf(line.c_str(), "sendwhite %d", &first) == 1) {
    if (first < 0 || first > 255) {
      Serial.println("ERROR: candidate must be 0..255");
      return;
    }
    Serial.printf("TX WHITE candidate=%d\n", first);
    const bool ok = sendCandidate(static_cast<uint8_t>(first), true);
    Serial.println(ok ? "TX complete" : "TX failed");
    return;
  }

  if (line.startsWith("sendrgb ") &&
      sscanf(line.c_str(), "sendrgb %d %d %d %d", &first, &red, &green,
             &blue) == 4) {
    if (first < 0 || first > 255 || red < 0 || red > 255 || green < 0 ||
        green > 255 || blue < 0 || blue > 255) {
      Serial.println("ERROR: key, R, G and B must each be 0..255");
      return;
    }
    Serial.printf("TX RGB candidate=%d rgb=(%d,%d,%d)\n", first, red, green,
                  blue);
    const bool ok = sendRgbCandidate(
        static_cast<uint8_t>(first), static_cast<uint8_t>(red),
        static_cast<uint8_t>(green), static_cast<uint8_t>(blue));
    Serial.println(ok ? "TX complete" : "TX failed");
    return;
  }

  if (line.startsWith("probe ") &&
      sscanf(line.c_str(), "probe %d %d", &first, &last) == 2) {
    if (first < 0 || last < first || last > 255) {
      Serial.println("ERROR: expected probe A B with 0 <= A <= B <= 255");
      return;
    }
    static constexpr uint8_t colors[4][3] = {
        {255, 0, 0}, {0, 255, 0}, {0, 0, 255}, {255, 0, 255}};
    const int count = last - first + 1;
    Serial.printf("PROBE begin=%d end=%d; quarters=red/green/blue/purple\n",
                  first, last);
    for (int candidate = first; candidate <= last; ++candidate) {
      int quarter = ((candidate - first) * 4) / count;
      if (quarter > 3) quarter = 3;
      Serial.printf("TX PROBE candidate=%d quarter=%d\n", candidate,
                    quarter + 1);
      if (!sendRgbCandidate(candidate, colors[quarter][0], colors[quarter][1],
                            colors[quarter][2])) {
        Serial.println("PROBE aborted: RADIO error");
        return;
      }
      delay(kCandidateDwellMs);
    }
    Serial.println("PROBE complete");
    return;
  }

  if (line.startsWith("range ") &&
      sscanf(line.c_str(), "range %d %d", &first, &last) == 2) {
    if (first < 0 || last < first || last > 255) {
      Serial.println("ERROR: expected range A B with 0 <= A <= B <= 255");
      return;
    }
    Serial.printf("RANGE begin=%d end=%d\n", first, last);
    for (int candidate = first; candidate <= last; ++candidate) {
      Serial.printf("TX candidate=%d\n", candidate);
      if (!sendCandidate(static_cast<uint8_t>(candidate))) {
        Serial.println("RANGE aborted: RADIO error");
        return;
      }
      delay(kCandidateDwellMs);
    }
    Serial.println("RANGE complete");
    return;
  }


  if (line.startsWith("rangewhite ") &&
      sscanf(line.c_str(), "rangewhite %d %d", &first, &last) == 2) {
    if (first < 0 || last < first || last > 255) {
      Serial.println("ERROR: expected range A B with 0 <= A <= B <= 255");
      return;
    }
    Serial.printf("WHITE RANGE begin=%d end=%d\n", first, last);
    for (int candidate = first; candidate <= last; ++candidate) {
      Serial.printf("TX WHITE candidate=%d\n", candidate);
      if (!sendCandidate(static_cast<uint8_t>(candidate), true)) {
        Serial.println("WHITE RANGE aborted: RADIO error");
        return;
      }
      delay(kCandidateDwellMs);
    }
    Serial.println("WHITE RANGE complete");
    return;
  }

  Serial.println("ERROR: unknown command; enter help");
}

}  // namespace

void setup() {
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, HIGH);

  Serial.begin(115200);
  const uint32_t waitStartedAt = millis();
  while (!Serial && millis() - waitStartedAt < 5000) delay(10);

  Serial.println();
  Serial.println("=== JFKJ raw 2.4 GHz research console ===");
  printInfo();
  printHelp();
}

void loop() {
  while (Serial.available()) {
    const char c = static_cast<char>(Serial.read());
    if (c == '\n' || c == '\r') {
      if (commandLine.length()) {
        handleCommand(commandLine);
        commandLine = "";
      }
    } else if (commandLine.length() < 80) {
      commandLine += c;
    }
  }
  delay(2);
}
