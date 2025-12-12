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

template <typename T, uint32_t CAPACITY, typename INDEX_TYPE = uint32_t>
class LockFreeQueue {
public:
    static constexpr bool isPowerOfTwo(uint32_t n) { return (n & (n - 1)) == 0; }
    static_assert(isPowerOfTwo(CAPACITY), "Capacity must be a power of 2");
    static_assert(std::is_unsigned<INDEX_TYPE>::value, "Index type must be unsigned");

    bool pop(T &val) {
        if (isEmpty()){
            return false;
        } else {
            val = std::move(buffer[mask(readCounter)]);
            ++readCounter;
            return true;
        }
    }

    bool push(const T& item) {
        if (isFull()){
            return false;
        } else {
            buffer[mask(writeCounter)] = item;
            ++writeCounter;
            return true;
        }
    }

    bool push(T&& item) {
        if (isFull()){
            return false;
        } else {
            buffer[mask(writeCounter)] = std::move(item);
            ++writeCounter;
            return true;
        }
    }

    bool peek(T &item) const {
        if (isEmpty()){
            return false;
        } else {
            item = buffer[mask(readCounter)];
            return true;
        }
    }

    INDEX_TYPE size() const {
        return writeCounter - readCounter;
    };

    bool empty() const { return isEmpty(); }
    bool full() const { return isFull(); }

    void clear() {
        readCounter = writeCounter.load();
    }

private:
    bool isEmpty() const { return readCounter == writeCounter; }
    bool isFull() const { return size() == CAPACITY; }
    INDEX_TYPE mask(INDEX_TYPE n) const { return static_cast<INDEX_TYPE>(n & (CAPACITY - 1)); }

    T buffer[CAPACITY];
    std::atomic<INDEX_TYPE> writeCounter { 0 };
    std::atomic<INDEX_TYPE> readCounter { 0 };
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

private:
    LockFreeQueue<std::unique_ptr<T>, POOL_SIZE> pool;
    std::unique_ptr<T> objects[POOL_SIZE];
};

#endif //RHYTHMGAME_LOCKFREEQUEUE_H