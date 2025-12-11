#ifndef RYUJINX_DYNAMIC_AUDIO_QUEUE_H
#define RYUJINX_DYNAMIC_AUDIO_QUEUE_H

#include <atomic>
#include <memory>
#include <vector>
#include <mutex>
#include <cstdint>

namespace RyujinxOboe {

struct AudioBlock {
    static constexpr size_t DEFAULT_BLOCK_SIZE = 4096;  // 4KB默认大小
    
    std::vector<uint8_t> data;
    size_t data_size = 0;
    size_t data_played = 0;
    int32_t sample_format = 0;
    bool consumed = true;
    
    AudioBlock() : data(DEFAULT_BLOCK_SIZE) {}
    
    void clear() {
        data_size = 0;
        data_played = 0;
        consumed = true;
    }
    
    size_t available() const {
        return data_size - data_played;
    }
    
    void ensure_capacity(size_t required_size) {
        if (data.size() < required_size) {
            // 扩容到最接近的2的幂次方
            size_t new_size = 1;
            while (new_size < required_size) {
                new_size <<= 1;
            }
            data.resize(new_size);
        }
    }
};

class DynamicAudioQueue {
private:
    struct Node {
        std::unique_ptr<AudioBlock> block;
        std::unique_ptr<Node> next;
        
        Node() : block(std::make_unique<AudioBlock>()) {}
    };
    
    // 环形缓冲区：头部指向可读取的位置，尾部指向可写入的位置
    std::unique_ptr<Node> m_head;
    std::unique_ptr<Node> m_tail;
    Node* m_head_ptr = nullptr;
    Node* m_tail_ptr = nullptr;
    
    // 预分配节点池
    std::vector<std::unique_ptr<Node>> m_node_pool;
    std::atomic<size_t> m_pool_used{0};
    
    // 统计信息
    std::atomic<size_t> m_total_blocks{0};
    std::atomic<size_t> m_dropped_blocks{0};
    std::atomic<size_t> m_max_queue_size{0};
    
    // 同步
    mutable std::mutex m_resize_mutex;
    std::atomic<bool> m_dynamic_growth_enabled{true};
    
    // 配置
    static constexpr size_t INITIAL_POOL_SIZE = 64;
    static constexpr size_t MAX_POOL_SIZE = 1024;
    static constexpr size_t GROWTH_FACTOR = 2;
    
    void initialize_pool(size_t size);
    Node* acquire_node();
    void release_node(Node* node);
    
public:
    DynamicAudioQueue();
    ~DynamicAudioQueue() = default;
    
    bool push(std::unique_ptr<AudioBlock> block);
    bool pop(std::unique_ptr<AudioBlock>& block);
    bool peek(std::unique_ptr<AudioBlock>& block) const;
    
    size_t size() const;
    bool empty() const;
    void clear();
    
    // 动态调整
    void enable_dynamic_growth(bool enable) { m_dynamic_growth_enabled.store(enable); }
    void reserve(size_t capacity);
    
    // 统计信息
    size_t get_total_blocks() const { return m_total_blocks.load(); }
    size_t get_dropped_blocks() const { return m_dropped_blocks.load(); }
    size_t get_max_queue_size() const { return m_max_queue_size.load(); }
    float get_utilization() const;
    
    // 性能优化
    void optimize_for_latency();
    void optimize_for_throughput();
};

} // namespace RyujinxOboe

#endif // RYUJINX_DYNAMIC_AUDIO_QUEUE_H