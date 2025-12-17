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
#include <type_traits>
#include <concepts>
#include <utility>

template <typename T, uint32_t CAPACITY, typename INDEX_TYPE = uint32_t>
requires (std::unsigned_integral<INDEX_TYPE>)
class LockFreeQueue {
public:
    static consteval bool isPowerOfTwo(uint32_t n) noexcept { 
        return (n & (n - 1)) == 0; 
    }
    
    static_assert(isPowerOfTwo(CAPACITY), "Capacity must be a power of 2");
    static_assert(CAPACITY > 0, "Capacity must be greater than 0");

    [[nodiscard]] bool pop(T &val) noexcept {
        if (isEmpty()) {
            return false;
        } else {
            val = std::move(buffer[mask(readCounter)]);
            ++readCounter;
            return true;
        }
    }

    [[nodiscard]] bool push(const T& item) noexcept {
        if (isFull()) {
            return false;
        } else {
            buffer[mask(writeCounter)] = item;
            ++writeCounter;
            return true;
        }
    }

    [[nodiscard]] bool push(T&& item) noexcept {
        if (isFull()) {
            return false;
        } else {
            buffer[mask(writeCounter)] = std::move(item);
            ++writeCounter;
            return true;
        }
    }

    template <typename... Args>
    [[nodiscard]] bool emplace(Args&&... args) noexcept {
        if (isFull()) {
            return false;
        } else {
            buffer[mask(writeCounter)] = T{std::forward<Args>(args)...};
            ++writeCounter;
            return true;
        }
    }

    [[nodiscard]] bool peek(T &item) const noexcept {
        if (isEmpty()) {
            return false;
        } else {
            item = buffer[mask(readCounter)];
            return true;
        }
    }

    [[nodiscard]] INDEX_TYPE size() const noexcept {
        return writeCounter.load(std::memory_order_acquire) - 
               readCounter.load(std::memory_order_acquire);
    }

    [[nodiscard]] bool empty() const noexcept { return isEmpty(); }
    [[nodiscard]] bool full() const noexcept { return isFull(); }

    void clear() noexcept {
        readCounter.store(writeCounter.load(std::memory_order_acquire), 
                         std::memory_order_release);
    }

    [[nodiscard]] constexpr INDEX_TYPE capacity() const noexcept { return CAPACITY; }

private:
    [[nodiscard]] bool isEmpty() const noexcept { 
        return readCounter.load(std::memory_order_acquire) == 
               writeCounter.load(std::memory_order_acquire); 
    }
    
    [[nodiscard]] bool isFull() const noexcept { return size() == CAPACITY; }
    
    [[nodiscard]] constexpr INDEX_TYPE mask(INDEX_TYPE n) const noexcept { 
        return static_cast<INDEX_TYPE>(n & (CAPACITY - 1)); 
    }

    alignas(64) T buffer[CAPACITY];
    alignas(64) std::atomic<INDEX_TYPE> writeCounter{0};
    alignas(64) std::atomic<INDEX_TYPE> readCounter{0};
};

template<typename T, uint32_t POOL_SIZE>
requires (POOL_SIZE > 0)
class LockFreeObjectPool {
public:
    LockFreeObjectPool() {
        for (uint32_t i = 0; i < POOL_SIZE; ++i) {
            objects[i] = std::make_unique<T>();
            pool.push(std::move(objects[i]));
        }
    }

    [[nodiscard]] std::unique_ptr<T> acquire() noexcept {
        std::unique_ptr<T> obj;
        if (pool.pop(obj)) {
            return obj;
        }
        return std::make_unique<T>();
    }

    [[nodiscard]] bool release(std::unique_ptr<T> obj) noexcept {
        if (obj) {
            obj->clear();
            return pool.push(std::move(obj));
        }
        return false;
    }

    [[nodiscard]] uint32_t available() const noexcept {
        return pool.size();
    }

    [[nodiscard]] constexpr uint32_t capacity() const noexcept { return POOL_SIZE; }

private:
    LockFreeQueue<std::unique_ptr<T>, POOL_SIZE> pool;
    std::unique_ptr<T> objects[POOL_SIZE];
};

#endif // RHYTHMGAME_LOCKFREEQUEUE_H