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
    
    bool pop_batch(T* output, uint32_t count, uint32_t* actual_count) {
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_relaxed);
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        
        INDEX_TYPE available = currentWrite - currentRead;
        if (available == 0) {
            *actual_count = 0;
            return false;
        }
        
        uint32_t to_pop = std::min(static_cast<uint32_t>(available), count);
        for (uint32_t i = 0; i < to_pop; ++i) {
            output[i] = std::move(buffer[mask(currentRead + i)]);
        }
        
        readCounter.store(currentRead + to_pop, std::memory_order_release);
        *actual_count = to_pop;
        return true;
    }
    
    bool push_batch(const T* items, uint32_t count) {
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_relaxed);
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_acquire);
        
        INDEX_TYPE available_space = CAPACITY - (currentWrite - currentRead);
        if (available_space < count) {
            return false;
        }
        
        for (uint32_t i = 0; i < count; ++i) {
            buffer[mask(currentWrite + i)] = items[i];
        }
        
        writeCounter.store(currentWrite + count, std::memory_order_release);
        return true;
    }
    
    bool push_front(T&& item) {
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_relaxed);
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        
        if (currentRead == 0) {
            return false;
        }
        
        INDEX_TYPE newRead = currentRead - 1;
        buffer[mask(newRead)] = std::move(item);
        readCounter.store(newRead, std::memory_order_release);
        return true;
    }
    
    bool push_front(const T& item) {
        INDEX_TYPE currentRead = readCounter.load(std::memory_order_relaxed);
        INDEX_TYPE currentWrite = writeCounter.load(std::memory_order_acquire);
        
        if (currentRead == 0) {
            return false;
        }
        
        INDEX_TYPE newRead = currentRead - 1;
        buffer[mask(newRead)] = item;
        readCounter.store(newRead, std::memory_order_release);
        return true;
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
    
    bool acquire_batch(std::unique_ptr<T>* output, uint32_t count, uint32_t* actual_count) {
        uint32_t acquired = 0;
        for (uint32_t i = 0; i < count; ++i) {
            std::unique_ptr<T> obj;
            if (pool.pop(obj)) {
                output[acquired++] = std::move(obj);
            } else {
                break;
            }
        }
        
        *actual_count = acquired;
        return acquired > 0;
    }
    
    bool release_batch(std::unique_ptr<T>* items, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            if (items[i]) {
                items[i]->clear();
                if (!pool.push(std::move(items[i]))) {
                    return false;
                }
            }
        }
        return true;
    }

private:
    LockFreeQueue<std::unique_ptr<T>, POOL_SIZE> pool;
    std::unique_ptr<T> objects[POOL_SIZE];
};

#endif //RHYTHMGAME_LOCKFREEQUEUE_H