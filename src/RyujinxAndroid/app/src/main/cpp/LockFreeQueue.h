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
    }

private:
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

    void preallocate(uint32_t count) {
        for (uint32_t i = 0; i < count && available() < POOL_SIZE; ++i) {
            auto obj = std::make_unique<T>();
            pool.push(std::move(obj));
        }
    }

private:
    LockFreeQueue<std::unique_ptr<T>, POOL_SIZE> pool;
    std::unique_ptr<T> objects[POOL_SIZE];
};

#endif //RHYTHMGAME_LOCKFREEQUEUE_H