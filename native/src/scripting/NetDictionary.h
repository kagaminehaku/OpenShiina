// A dictionary that is enumerated in the order .NET's Dictionary<TKey, TValue> enumerates: its
// entries array in order, an entry removed leaving a hole that the next added key takes (the hole
// made last first). The C# player enumerates some of its dictionaries (timers, sounds) and acts in
// that order, so the port keeps it.
#pragma once

#include <cstddef>
#include <unordered_map>
#include <utility>
#include <vector>

namespace openshiina::scripting {

template <typename K, typename V> class NetDictionary
{
public:
    struct Entry
    {
        K Key;
        V Value;
        bool Used = false;
    };

    size_t Count() const { return m_index.size(); }

    bool Contains(const K& key) const { return m_index.count(key) != 0; }

    V* Find(const K& key)
    {
        auto found = m_index.find(key);
        return found == m_index.end() ? nullptr : &m_entries[found->second].Value;
    }

    const V* Find(const K& key) const
    {
        auto found = m_index.find(key);
        return found == m_index.end() ? nullptr : &m_entries[found->second].Value;
    }

    V GetValueOrDefault(const K& key, V otherwise = V()) const
    {
        const V* value = Find(key);
        return value ? *value : otherwise;
    }

    /// The value of `key`, added as V() when missing (dictionary[key] = ... in C#).
    V& operator[](const K& key)
    {
        auto found = m_index.find(key);
        if (found != m_index.end())
            return m_entries[found->second].Value;
        size_t at;
        if (!m_free.empty())
        {
            at = m_free.back();
            m_free.pop_back();
            m_entries[at] = Entry{key, V(), true};
        }
        else
        {
            at = m_entries.size();
            m_entries.push_back(Entry{key, V(), true});
        }
        m_index.emplace(key, at);
        return m_entries[at].Value;
    }

    bool Remove(const K& key)
    {
        auto found = m_index.find(key);
        if (found == m_index.end())
            return false;
        m_entries[found->second] = Entry{};
        m_free.push_back(found->second);
        m_index.erase(found);
        return true;
    }

    void Clear()
    {
        m_entries.clear();
        m_free.clear();
        m_index.clear();
    }

    /// The entries in .NET's order (each of them a copy, as ToList() makes).
    std::vector<std::pair<K, V>> Items() const
    {
        std::vector<std::pair<K, V>> items;
        items.reserve(m_index.size());
        for (const auto& entry : m_entries)
            if (entry.Used)
                items.emplace_back(entry.Key, entry.Value);
        return items;
    }

    template <typename F> void ForEach(F f) const
    {
        for (const auto& entry : m_entries)
            if (entry.Used)
                f(entry.Key, entry.Value);
    }

    template <typename F> void ForEach(F f)
    {
        for (auto& entry : m_entries)
            if (entry.Used)
                f(entry.Key, entry.Value);
    }

private:
    std::vector<Entry> m_entries;
    std::vector<size_t> m_free;
    std::unordered_map<K, size_t> m_index;
};

} // namespace openshiina::scripting
