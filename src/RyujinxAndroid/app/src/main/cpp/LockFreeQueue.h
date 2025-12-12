/*
 * Copyright 2018 The Android Open Source Project
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#ifndef RHYTHMGAME_LOCKFREEQUEUE_H
#define RHYTHMGAME_LOCKFREEQUEUE_H

#include <cstdint>
#include <atomic>
#include <memory>
#include <cassert>

template <typename T, uint32_t CAPACITY, typename INDEX_TYPE = uint32_t>
class LockFreeQueue {
public:
    static constexpr bool isPowerOfTwo(uint32_t n) { return (n & (n - 1)) == 0; }
    static_assert(isPowerOfTwo(CAPACITY), "Capacity must be a power of 2");
    static_assert(std::is_unsigned<INDEX_TYPE>::value, "Index type must be unsigned");
    
    LockFreeQueue() : writeCounter(0), readCounter(0) {
        for (uint32_t i = 0; i < CAPACITY; ++i) {
            buffer[i] = T();
        }
    }
    
    ~LockFreeQueue() {
        clear();
    }

    bool pop(T &val) {
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_relaxed);
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        
        if (currentRead == currentWrite) {
            return false;
        }
        
        val = std::move(buffer[mask(currentRead)]);
        readCounter.store(currentRead + 1, std::memory_order_release);
        return true;
    }

    bool push(const T& item) {
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_relaxed);
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_acquire);
        
        if ((currentWrite - currentRead) == CAPACITY) {
            return false;
        }
        
        buffer[mask(currentWrite)] = item;
        writeCounter.store(currentWrite + 1, std::memory_order_release);
        return true;
    }

    bool push(T&& item) {
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_relaxed);
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_acquire);
        
        if ((currentWrite - currentRead) == CAPACITY) {
            return false;
        }
        
        buffer[mask(currentWrite)] = std::move(item);
        writeCounter.store(currentWrite + 1, std::memory_order_release);
        return true;
    }

    bool peek(T &item) const {
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_acquire);
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        
        if (currentRead == currentWrite) {
            return false;
        }
        
        item = buffer[mask(currentRead)];
        return true;
    }

    INDEX_TYPE size() const {
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_acquire);
        return currentWrite - currentRead;
    };

    bool empty() const { 
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_acquire);
        return currentRead == currentWrite; 
    }
    
    bool full() const { 
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_acquire);
        return (currentWrite - currentRead) == CAPACITY; 
    }

    void clear() {
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        readCounter.store(currentWrite, std::memory_order_release);
        
        for (INDEX_TYPE i = 0; i < CAPACITY; ++i) {
            buffer[i] = T();
        }
    }
    
    uint32_t capacity() const {
        return CAPACITY;
    }
    
    float load_factor() const {
        return static_cast<float>(size()) / CAPACITY;
    }

private:
    INDEX_TYPE mask(INDEX_TYPE n) const { 
        return static_cast<INDEX_TYPE>(n & (CAPACITY - 1)); 
    }

    T buffer[CAPACITY];
    alignas(64) std::atomic<INDEX_TYPE> writeCounter;
    alignas(64) std::atomic<INDEX_TYPE> readCounter;
};

template<typename T, uint32_t POOL_SIZE>
class LockFreeObjectPool {
public:
    LockFreeObjectPool() {
        for (uint32_t i = 0; i < POOL_SIZE; ++i) {
            objects[i] = std::make_unique<T>();
            pool.push(std::move(objects[i]));
        }
    }
    
    ~LockFreeObjectPool() {
        clear();
    }

    std::unique_ptr<T> acquire() {
        std::unique_ptr<T> obj;
        if (pool.pop(obj)) {
            return obj;
        }
        return std::make_unique<T>();
    }

    bool release(std::unique_ptr<T> obj) {
        if (obj) {
            obj->clear();
            return pool.push(std::move(obj));
        }
        return false;
    }

    uint32_t available() const {
        return pool.size();
    }
    
    uint32_t capacity() const {
        return POOL_SIZE;
    }
    
    float usage() const {
        return static_cast<float>(available()) / POOL_SIZE;
    }

    void preallocate(uint32_t count) {
        for (uint32_t i = 0; i < count && available() < POOL_SIZE; ++i) {
            auto obj = std::make_unique<T>();
            pool.push(std::move(obj));
        }
    }
    
    void clear() {
        pool.clear();
        for (uint32_t i = 0; i < POOL_SIZE; ++i) {
            objects[i].reset();
        }
    }

private:
    LockFreeQueue<std::unique_ptr<T>, POOL_SIZE> pool;
    std::unique_ptr<T> objects[POOL_SIZE];
};

#endif //RHYTHMGAME_LOCKFREEQUEUE_H