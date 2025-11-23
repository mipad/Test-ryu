// LockFreeQueue.h - 基于段的无锁动态扩容队列
#ifndef RHYTHMGAME_LOCKFREEQUEUE_H
#define RHYTHMGAME_LOCKFREEQUEUE_H

#include <atomic>
#include <memory>
#include <cstdint>
#include <array>

template <typename T>
class SegmentBasedLockFreeQueue {
private:
    static constexpr size_t DEFAULT_SEGMENT_SIZE = 64;
    static constexpr size_t MAX_SEGMENTS = 1024; // 防止无限扩容

    struct Segment {
        std::atomic<Segment*> next;
        std::atomic<size_t> head;
        std::atomic<size_t> tail;
        size_t capacity;
        std::unique_ptr<T[]> data;

        Segment(size_t cap = DEFAULT_SEGMENT_SIZE) 
            : next(nullptr), head(0), tail(0), capacity(cap), data(std::make_unique<T[]>(cap)) {}

        bool is_full() const { return tail.load(std::memory_order_acquire) >= capacity; }
        bool is_empty() const { return head.load(std::memory_order_acquire) >= tail.load(std::memory_order_acquire); }
        
        size_t size() const { 
            return tail.load(std::memory_order_acquire) - head.load(std::memory_order_acquire); 
        }
        
        size_t remaining() const { 
            return capacity - tail.load(std::memory_order_acquire); 
        }

        bool try_push(const T& item) {
            size_t current_tail = tail.load(std::memory_order_relaxed);
            if (current_tail >= capacity) return false;
            
            data[current_tail] = item;
            tail.store(current_tail + 1, std::memory_order_release);
            return true;
        }

        bool try_push(T&& item) {
            size_t current_tail = tail.load(std::memory_order_relaxed);
            if (current_tail >= capacity) return false;
            
            data[current_tail] = std::move(item);
            tail.store(current_tail + 1, std::memory_order_release);
            return true;
        }

        bool try_pop(T& item) {
            size_t current_head = head.load(std::memory_order_relaxed);
            size_t current_tail = tail.load(std::memory_order_acquire);
            
            if (current_head >= current_tail) return false;
            
            item = std::move(data[current_head]);
            head.store(current_head + 1, std::memory_order_release);
            return true;
        }

        void clear() {
            head.store(0, std::memory_order_relaxed);
            tail.store(0, std::memory_order_release);
        }
    };

public:
    SegmentBasedLockFreeQueue(size_t initial_segment_size = DEFAULT_SEGMENT_SIZE) 
        : _segment_size(initial_segment_size) {
        Segment* first_segment = new Segment(_segment_size);
        _head.store(first_segment, std::memory_order_relaxed);
        _tail.store(first_segment, std::memory_order_relaxed);
        _segment_count.store(1, std::memory_order_relaxed);
    }

    ~SegmentBasedLockFreeQueue() {
        Segment* current = _head.load(std::memory_order_relaxed);
        while (current) {
            Segment* next = current->next.load(std::memory_order_relaxed);
            delete current;
            current = next;
        }
    }

    // 禁用拷贝和移动
    SegmentBasedLockFreeQueue(const SegmentBasedLockFreeQueue&) = delete;
    SegmentBasedLockFreeQueue& operator=(const SegmentBasedLockFreeQueue&) = delete;

    bool push(const T& item) {
        return push_impl(item);
    }

    bool push(T&& item) {
        return push_impl(std::move(item));
    }

    bool pop(T& item) {
        Segment* current_head = _head.load(std::memory_order_acquire);
        
        while (current_head) {
            if (current_head->try_pop(item)) {
                // 如果当前段已空且有下一个段，尝试回收当前段
                if (current_head->is_empty() && current_head->next.load(std::memory_order_acquire)) {
                    try_advance_head();
                }
                return true;
            }
            
            // 当前段为空，检查是否有下一个段
            Segment* next_segment = current_head->next.load(std::memory_order_acquire);
            if (!next_segment) {
                return false; // 没有更多数据
            }
            
            // 尝试切换到下一个段
            if (_head.compare_exchange_weak(current_head, next_segment, 
                                          std::memory_order_acq_rel, 
                                          std::memory_order_acquire)) {
                // 成功切换head，回收旧段
                reclaim_segment(current_head);
            }
            
            current_head = _head.load(std::memory_order_acquire);
        }
        
        return false;
    }

    bool empty() const {
        Segment* current_head = _head.load(std::memory_order_acquire);
        Segment* current_tail = _tail.load(std::memory_order_acquire);
        
        // 队列为空的条件：head和tail指向同一个段，且该段为空
        return (current_head == current_tail) && current_head->is_empty();
    }

    size_t size() const {
        size_t total_size = 0;
        Segment* current = _head.load(std::memory_order_acquire);
        
        while (current) {
            total_size += current->size();
            current = current->next.load(std::memory_order_acquire);
        }
        
        return total_size;
    }

    void clear() {
        // 简单的清空方法：保留第一个段，删除其他段
        Segment* first_segment = _head.load(std::memory_order_acquire);
        Segment* current_tail = _tail.load(std::memory_order_acquire);
        
        if (first_segment) {
            first_segment->clear();
            
            // 如果head和tail不是同一个段，需要重置tail
            if (first_segment != current_tail) {
                Segment* next_segment = first_segment->next.load(std::memory_order_acquire);
                first_segment->next.store(nullptr, std::memory_order_release);
                _tail.store(first_segment, std::memory_order_release);
                
                // 删除其他段
                Segment* to_delete = next_segment;
                while (to_delete) {
                    Segment* next = to_delete->next.load(std::memory_order_relaxed);
                    delete to_delete;
                    to_delete = next;
                }
                
                _segment_count.store(1, std::memory_order_release);
            }
        }
    }

    // 批量操作接口
    template <typename InputIt>
    size_t push_bulk(InputIt first, InputIt last) {
        size_t count = 0;
        for (auto it = first; it != last; ++it) {
            if (!push(*it)) break;
            count++;
        }
        return count;
    }

    template <typename OutputIt>
    size_t pop_bulk(OutputIt first, size_t max_count) {
        size_t count = 0;
        T item;
        
        for (size_t i = 0; i < max_count; ++i) {
            if (!pop(item)) break;
            *first++ = std::move(item);
            count++;
        }
        
        return count;
    }

private:
    template <typename U>
    bool push_impl(U&& item) {
        Segment* current_tail = _tail.load(std::memory_order_acquire);
        
        while (true) {
            // 尝试在当前尾段插入
            if (current_tail->try_push(std::forward<U>(item))) {
                return true;
            }
            
            // 当前段已满，尝试添加新段
            Segment* next_segment = current_tail->next.load(std::memory_order_acquire);
            if (!next_segment) {
                // 需要创建新段
                if (_segment_count.load(std::memory_order_acquire) >= MAX_SEGMENTS) {
                    return false; // 达到最大段数限制
                }
                
                size_t new_segment_size = _segment_size * 2; // 指数增长
                Segment* new_segment = new Segment(new_segment_size);
                
                if (current_tail->next.compare_exchange_weak(next_segment, new_segment,
                                                           std::memory_order_acq_rel,
                                                           std::memory_order_acquire)) {
                    // 成功链接新段
                    _tail.store(new_segment, std::memory_order_release);
                    _segment_count.fetch_add(1, std::memory_order_acq_rel);
                    current_tail = new_segment;
                    continue; // 重试插入
                } else {
                    // 其他线程已经添加了段
                    delete new_segment;
                    current_tail = next_segment;
                }
            } else {
                // 帮助推进tail指针
                _tail.compare_exchange_weak(current_tail, next_segment,
                                          std::memory_order_acq_rel,
                                          std::memory_order_acquire);
                current_tail = next_segment;
            }
        }
    }

    void try_advance_head() {
        Segment* current_head = _head.load(std::memory_order_acquire);
        Segment* next_segment = current_head->next.load(std::memory_order_acquire);
        
        if (next_segment && current_head->is_empty()) {
            if (_head.compare_exchange_weak(current_head, next_segment,
                                          std::memory_order_acq_rel,
                                          std::memory_order_acquire)) {
                reclaim_segment(current_head);
            }
        }
    }

    void reclaim_segment(Segment* segment) {
        // 在实际生产环境中，这里应该使用hazard pointer或epoch-based回收
        // 这里简化处理，直接删除（假设没有其他线程访问）
        delete segment;
        _segment_count.fetch_sub(1, std::memory_order_acq_rel);
    }

private:
    std::atomic<Segment*> _head;
    std::atomic<Segment*> _tail;
    std::atomic<size_t> _segment_count;
    size_t _segment_size;
};

// 保持原有LockFreeObjectPool兼容性
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
        return static_cast<uint32_t>(pool.size());
    }

    void preallocate(uint32_t count) {
        for (uint32_t i = 0; i < count && available() < POOL_SIZE; ++i) {
            auto obj = std::make_unique<T>();
            pool.push(std::move(obj));
        }
    }

private:
    SegmentBasedLockFreeQueue<std::unique_ptr<T>> pool;
    std::unique_ptr<T> objects[POOL_SIZE];
};

#endif //RHYTHMGAME_LOCKFREEQUEUE_H