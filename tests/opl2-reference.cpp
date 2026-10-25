// Own test-only adapter for an independently supplied BSD-licensed YMFM build.
// No YMFM implementation is linked into or distributed with the emulator.
// Input: nanoseconds, address/data offset (0/1), value. Output: signed32 mono
// chip samples at master/72 Hz, before our host mixer/filter or volume controls.
#include "ymfm_opl.h"
#include <algorithm>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <limits>
#include <vector>

struct Interface : ymfm::ymfm_interface {
    uint64_t now = 0, expiry[2] = {UINT64_MAX,UINT64_MAX};
    void ymfm_set_timer(uint32_t timer,int32_t clocks) override {
        expiry[timer] = clocks < 0 ? UINT64_MAX : now + uint32_t(clocks);
    }
    void advance(uint64_t target) {
        while(std::min(expiry[0],expiry[1]) <= target) {
            unsigned timer = expiry[0] <= expiry[1] ? 0 : 1;
            now = expiry[timer]; expiry[timer] = UINT64_MAX;
            m_engine->engine_timer_expired(timer);
        }
        now = target;
    }
};
struct Event { uint64_t time; uint32_t offset,value; };
int main(int argc,char** argv) {
    if(argc != 4) { std::cerr << "usage: opl2-reference events.txt chip.s32 end-nanoseconds\n"; return 2; }
    std::ifstream input(argv[1]); std::ofstream output(argv[2],std::ios::binary);
    if(!input || !output) return 3;
    std::vector<Event> events; Event event;
    while(input >> event.time >> event.offset >> event.value) events.push_back(event);
    Interface interface; ymfm::ym3812 chip(interface); chip.reset();
    uint64_t end = std::stoull(argv[3]); size_t cursor = 0;
    uint64_t count = end*3579545/72000000000ULL;
    // Use native chip clocks; writes are committed on the first master tick at
    // or after their observed OUT. Native-slot latency remains YMFM's own model.
    for(uint64_t index = 1; index <= count; ++index) {
        uint64_t clock = index*72;
        while(cursor < events.size()) {
            const Event& e = events[cursor];
            uint64_t due = (e.time*3579545+999999999)/1000000000;
            if(due > clock) break;
            interface.advance(due); chip.write(e.offset,uint8_t(e.value)); ++cursor;
        }
        interface.advance(clock); ymfm::ym3812::output_data sample; chip.generate(&sample);
        int32_t value = sample.data[0]; output.write(reinterpret_cast<const char*>(&value),sizeof(value));
    }
    return output ? 0 : 4;
}
