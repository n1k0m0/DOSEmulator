// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
// Own test-only adapter. An independently supplied BSD YMFM never executes product audio.
// Input: nanoseconds, port offset 0..3, value. Output: native signed32 A/B/C/D frames.
#include "ymfm_opl.h"
#include <algorithm>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <vector>

struct Interface : ymfm::ymfm_interface {
    uint64_t now = 0, expiry[2] = {UINT64_MAX,UINT64_MAX};
    // Converts the isolated reference engine's relative timer request into a native-clock deadline.
    void ymfm_set_timer(uint32_t timer, int32_t clocks) override {
        expiry[timer] = clocks < 0 ? UINT64_MAX : now + uint32_t(clocks);
    }
    // Delivers every due timer in stable order before reaching the next observed guest write/sample.
    void advance(uint64_t target) {
        while(std::min(expiry[0],expiry[1]) <= target) {
            unsigned timer = expiry[0] <= expiry[1] ? 0 : 1;
            now = expiry[timer]; expiry[timer] = UINT64_MAX;
            m_engine->engine_timer_expired(timer);
        }
        now = target;
    }
};
struct Event { uint64_t time; uint32_t offset, value; };
// Replays only observed guest OUT events and exports four independent native reference buses.
int main(int argc, char** argv) {
    if(argc != 4) { std::cerr << "usage: opl3-reference events.txt quad.s32 end-nanoseconds\n"; return 2; }
    std::ifstream input(argv[1]); std::ofstream output(argv[2], std::ios::binary);
    if(!input || !output) return 3;
    std::vector<Event> events; Event event;
    while(input >> event.time >> event.offset >> event.value) events.push_back(event);
    Interface interface; ymfm::ymf262 chip(interface); chip.reset();
    static_assert(ymfm::ymf262::OUTPUTS == 4);
    uint64_t end = std::stoull(argv[3]); size_t cursor = 0;
    uint64_t count = end*14318180/288000000000ULL;
    for(uint64_t index = 1; index <= count; ++index) {
        uint64_t clock = index*288;
        while(cursor < events.size()) {
            const Event& e = events[cursor];
            uint64_t due = (e.time*14318180+999999999)/1000000000;
            if(due > clock) break;
            interface.advance(due); chip.write(e.offset,uint8_t(e.value)); ++cursor;
        }
        interface.advance(clock); ymfm::ymf262::output_data sample; chip.generate(&sample);
        for(unsigned channel=0;channel<4;++channel) {
            int32_t value=sample.data[channel];
            output.write(reinterpret_cast<const char*>(&value),sizeof(value));
        }
    }
    return output ? 0 : 4;
}
