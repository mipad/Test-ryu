#include "DynamicAudioQueue.h"
#include <algorithm>
#include <thread>

namespace RyujinxOboe {

DynamicAudioQueue::DynamicAudioQueue() {
    initialize_pool(INITIAL_POOL_SIZE);
    
    // 初始化环形缓冲区
    m_head = std::make_unique<Node>();
    m_tail = m_head.get();
    m_head_ptr = m_head.get();
    m_tail_ptr = m_head.get();
}

void DynamicAudioQueue::initialize_pool(size_t size) {
    std::lock_guard<std::mutex> lock(m_resize_mutex);
    
    size_t current_pool_size = m_node_pool.size();
    if (current_pool_size >= size) return;
    
    size_t to_add = size - current_pool_size;
    for (size_t i = 0; i < to_add; ++i) {
        m_node_pool.push_back(std::make_unique<Node>());
    }
}

DynamicAudioQueue::Node* DynamicAudioQueue::acquire_node() {
    size_t current_used = m_pool_used.load();
    
    // 尝试从池中获取节点
    if (current_used < m_node_pool.size()) {
        if (m_pool_used.compare_exchange_weak(current_used, current_used + 1)) {
            return m_node_pool[current_used].get();
        }
    }
    
    // 池耗尽，动态扩容
    std::lock_guard<std::mutex> lock(m_resize_mutex);
    
    if (m_pool_used.load() >= m_node_pool.size()) {
        if (m_node_pool.size() < MAX_POOL_SIZE) {
            size_t new_size = std::min(m_node_pool.size() * GROWTH_FACTOR, MAX_POOL_SIZE);
            initialize_pool(new_size);
        }
    }
    
    // 再次尝试获取
    current_used = m_pool_used.load();
    if (current_used < m_node_pool.size()) {
        m_pool_used.store(current_used + 1);
        return m_node_pool[current_used].get();
    }
    
    // 池已满且达到上限，动态创建新节点
    return new Node();
}

void DynamicAudioQueue::release_node(Node* node) {
    if (!node) return;
    
    // 检查是否是池中的节点
    auto is_in_pool = [&](Node* n) {
        for (const auto& pool_node : m_node_pool) {
            if (pool_node.get() == n) return true;
        }
        return false;
    };
    
    if (is_in_pool(node)) {
        node->block->clear();
        m_pool_used.fetch_sub(1);
    } else {
        // 动态创建的节点，直接删除
        delete node;
    }
}

bool DynamicAudioQueue::push(std::unique_ptr<AudioBlock> block) {
    if (!block) return false;
    
    // 检查队列是否已满（尾节点的下一个是头节点）
    if (m_tail_ptr->next.get() == m_head_ptr) {
        if (m_dynamic_growth_enabled.load()) {
            // 动态添加新节点
            std::lock_guard<std::mutex> lock(m_resize_mutex);
            
            Node* new_node = acquire_node();
            if (!new_node) {
                m_dropped_blocks.fetch_add(1);
                return false;
            }
            
            new_node->next = std::move(m_tail_ptr->next);
            m_tail_ptr->next = std::unique_ptr<Node>(new_node);
            m_tail_ptr = new_node;
        } else {
            // 动态增长禁用，丢弃最旧的数据
            std::unique_ptr<AudioBlock> discarded;
            if (pop(discarded)) {
                m_dropped_blocks.fetch_add(1);
                // 递归尝试推送
                return push(std::move(block));
            }
            return false;
        }
    }
    
    // 写入数据到尾部节点
    std::swap(m_tail_ptr->block, block);
    
    // 移动到下一个节点
    if (m_tail_ptr->next) {
        m_tail_ptr = m_tail_ptr->next.get();
    } else {
        // 环形缓冲区的最后一个节点，创建新的环
        Node* new_node = acquire_node();
        if (new_node) {
            new_node->next = std::move(m_head);
            m_tail_ptr->next = std::unique_ptr<Node>(new_node);
            m_tail_ptr = new_node;
            m_head = std::move(m_tail_ptr->next);
            m_head_ptr = m_head.get();
        }
    }
    
    m_total_blocks.fetch_add(1);
    
    // 更新最大队列大小
    size_t current_size = size();
    size_t max_size = m_max_queue_size.load();
    while (current_size > max_size) {
        if (m_max_queue_size.compare_exchange_weak(max_size, current_size)) {
            break;
        }
    }
    
    return true;
}

bool DynamicAudioQueue::pop(std::unique_ptr<AudioBlock>& block) {
    if (empty()) return false;
    
    // 读取头部节点的数据
    std::swap(m_head_ptr->block, block);
    
    // 移动到下一个节点
    if (m_head_ptr->next) {
        m_head_ptr = m_head_ptr->next.get();
    } else {
        // 到达尾部，重置到头部
        m_head_ptr = m_head.get();
    }
    
    return true;
}

bool DynamicAudioQueue::peek(std::unique_ptr<AudioBlock>& block) const {
    if (empty()) return false;
    
    if (m_head_ptr->block) {
        // 创建块的副本（浅复制）
        block = std::make_unique<AudioBlock>();
        block->data = m_head_ptr->block->data;
        block->data_size = m_head_ptr->block->data_size;
        block->data_played = m_head_ptr->block->data_played;
        block->sample_format = m_head_ptr->block->sample_format;
        block->consumed = m_head_ptr->block->consumed;
        return true;
    }
    
    return false;
}

size_t DynamicAudioQueue::size() const {
    // 遍历环形链表计算大小
    size_t count = 0;
    Node* current = m_head_ptr;
    Node* start = current;
    
    if (!current) return 0;
    
    do {
        if (!current->block->consumed) {
            count++;
        }
        
        if (current->next) {
            current = current->next.get();
        } else {
            break;
        }
    } while (current != start);
    
    return count;
}

bool DynamicAudioQueue::empty() const {
    return size() == 0;
}

void DynamicAudioQueue::clear() {
    Node* current = m_head_ptr;
    Node* start = current;
    
    if (!current) return;
    
    do {
        current->block->clear();
        
        if (current->next) {
            current = current->next.get();
        } else {
            break;
        }
    } while (current != start);
    
    // 重置指针
    m_head_ptr = m_head.get();
    m_tail_ptr = m_head.get();
    
    m_total_blocks.store(0);
}

void DynamicAudioQueue::reserve(size_t capacity) {
    std::lock_guard<std::mutex> lock(m_resize_mutex);
    initialize_pool(capacity);
}

float DynamicAudioQueue::get_utilization() const {
    size_t pool_size = m_node_pool.size();
    if (pool_size == 0) return 0.0f;
    
    size_t used = m_pool_used.load();
    return static_cast<float>(used) / pool_size;
}

void DynamicAudioQueue::optimize_for_latency() {
    // 延迟优化：减小缓冲区大小
    std::lock_guard<std::mutex> lock(m_resize_mutex);
    
    size_t target_size = std::max(INITIAL_POOL_SIZE / 2, static_cast<size_t>(16));
    if (m_node_pool.size() > target_size) {
        // 缩小池大小（保留已分配的节点）
        m_node_pool.resize(target_size);
    }
    
    // 确保头部和尾部指针有效
    if (m_head_ptr >= m_node_pool.data() + m_node_pool.size()) {
        m_head_ptr = m_head.get();
    }
    if (m_tail_ptr >= m_node_pool.data() + m_node_pool.size()) {
        m_tail_ptr = m_head.get();
    }
}

void DynamicAudioQueue::optimize_for_throughput() {
    // 吞吐量优化：增大缓冲区大小
    std::lock_guard<std::mutex> lock(m_resize_mutex);
    
    size_t target_size = std::min(m_node_pool.size() * GROWTH_FACTOR, MAX_POOL_SIZE);
    initialize_pool(target_size);
}

} // namespace RyujinxOboe