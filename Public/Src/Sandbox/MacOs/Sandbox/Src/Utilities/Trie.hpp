// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#ifndef PathCache_hpp
#define PathCache_hpp

#include <IOKit/IOLib.h>
#include <IOKit/IOService.h>

/*!
 * A node in a Trie.
 * Only accessible to its friend class Trie.
 */
class Node : public OSObject
{
    OSDeclareDefaultStructors(Node);

private:

    friend class Trie;

    static uint s_numUintNodes;
    static uint s_numPathNodes;

    /*!
     * The value 65 is chosen so that all ASCII characters between 32 (' ') and 122 ('z')
     * get a unique entry in the 'children_' array.  The formula for mapping a character
     * ch to an array index is:
     *
     *   toupper(ch) - 32
     */
    static const uint s_pathNodeChildrenCount = 65;

    /*! For 10 digits */
    static const uint s_uintNodeChildrenCount = 10;

    /*! Arbitrary value */
    OSObject *record_;

    /*! The length of the 'children_' array (i.e., the the number of allocated nodes) */
    uint childrenLength_;

    /*! Pre-allocated pointers to all possible children nodes. */
    Node **children_;

    uint length()     const { return childrenLength_; }
    Node** children() const { return children_; }

    bool init(uint numChildren);
    static Node* create(uint numChildren);

    static Node* createUintNode() { return create(s_uintNodeChildrenCount); }
    static Node* createPathNode() { return create(s_pathNodeChildrenCount); }

protected:

    void free() override;
};

// ================================== class Trie ==================================

/*!
 * A thread-safe, lock-free, dictionary implementation.
 *
 * Only 2 types of keys are allowed: (1) an unsigned integer, and (2) an ascii path.
 *
 * A value must be a pointer to an arbitrary OSObject.  Once an OSObject is added
 * to this trie, it is automatically retained by this trie; once it is removed, it is
 * automatically released by this trie; this is analogous to how OSDictionary works.
 *
 * Paths are considered case-insensitive.  Attempting to add a path with a non-ascii
 * character will fail gracefully by returning 'kTrieResultFailure'.
 *
 * Thread-safe.  Non-blocking.
 */
class Trie : public OSObject
{
    OSDeclareDefaultStructors(Trie);

public:

    typedef OSObject* (*factory_fn)(void *data);
    typedef void (*on_change_fn)(void *data, const int oldCount, const int newCount);
    typedef void (*for_each_fn)(void *data, uint64_t key, const OSObject *value);
    typedef bool (*filter_fn)(void *data, const OSObject *value);

    typedef enum {
        kTrieResultInserted,
        kTrieResultReplaced,
        kTrieResultRemoved,
        kTrieResultAlreadyEmpty,
        kTrieResultAlreadyExists,
        kTrieResultRace,
        kTrieResultFailure,
    } TrieResult;

    static void getUintNodeCounts(uint *count, double *sizeMB)
    {
        getNodeCounts(Node::s_numUintNodes, Node::s_uintNodeChildrenCount, count, sizeMB);
    }

    static void getPathNodeCounts(uint *count, double *sizeMB)
    {
        getNodeCounts(Node::s_numPathNodes, Node::s_pathNodeChildrenCount, count, sizeMB);
    }

private:

    static const uint BytesInAMegabyte = 1 << 20;

    static void getNodeCounts(uint count, uint numChildren, uint *outCount, double *outSizeMB)
    {
        *outCount = count;
        *outSizeMB = (1.0 * count * (sizeof(Node) + numChildren * sizeof(Node*))) / BytesInAMegabyte;
    }

    typedef enum { kUintTrie, kPathTrie } TrieKind;
    typedef void (*traverse_fn)(Trie*, void*, uint64_t key, Node*);

    /*! The root of the tree. */
    Node *root_;

    /*! The kind of keys this tree accepts */
    TrieKind kind_;

    /*! This is the size of the tree (i.e., number of values stored) and not the number of nodes in the tree. */
    uint size_;

    /*! Callback function (and associated payload) to call whenever count changes. */
    on_change_fn onChangeCallback_;

    /*! Payload for the 'onChangeCallback_' function */
    void *onChangeData_;

    /*! Creates and initialized a new Trie.  The return value indicates the success of the operation. */
    bool init(TrieKind kind);

    /*! Invokes the 'onChangeCallback_' if it's set and 'newCount' is different from 'oldCount' */
    void triggerOnChange(int oldCount, int newCount) const;

    /*!
     * Creates a child node of 'node' at position 'idx' if such a child node doesn't already exist.
     *
     * @param node The node which must contain a child at position 'idx'.  Must not be null.
     * @param idx Must be between 0 (inclusive) and 'node.length()' (exclusive); otherwise this method returns false.
     * @result Indicates the success of the operation (it's true IFF 'node' has a child at position 'idx').
     */
    bool ensureChildNodeExists(Node *node, int idx);

    /*!
     * Ensures that 'node' has its 'record_' field set to a non-null value.
     * If not already set, uses the 'factory' function to create a new value and assign it to the 'record_' field.
     *
     * @param node The node that must become sentinel.
     * @param factoryArgs Arguments to pass to the 'factory' function
     * @param factory Function to call to create a record to assign to 'node' in the case when no record has already been assigned.
     * @result
     *    - kTrieResultAlreadyExists : if 'node' already has a record
     *    - kTrieResultInserted      : if a new record was created and assigned to 'node'.
     */
    TrieResult makeSentinel(Node *node, void *factoryArgs, factory_fn factory);

    /*!
     * Returns the record already assigned to 'node' or, if no record is assigned to it, creates a new one by invoking
     * 'factory', assigns it to 'node', and returns it.
     *
     * IMPORTANT: The caller must not assume that 'factory' is invoked IFF its return value is assigned to 'node'.
     *            It is possible that 'factory' is invoked, but due to a race some other record gets assigned to this
     *            node first; in that case, the OSObject returned by 'factory' is released and simply ignored.
     *
     * @result The record associated with 'node' or NULL if 'node' is NULL.
     */
    OSObject* getOrAdd(Node *node, void *factoryArgs, factory_fn factory, TrieResult *result);

    /*!
     * Returns the record associated with 'node' or NULL if either 'node' is NULL or no record is associated with it.
     */
    OSObject* get(Node *node);

    /*!
     * Attempts to associate 'value' with 'node', even if there is already a value associated with 'node'.
     *
     * If either 'node' or 'value' is NULL, the result is 'kTrieResultFailure'.
     *
     * If 'value' has been associated with 'node' and there wasn't a record previously associated with 'node':
     * retains 'value', increments size, and returns 'kTrieResultInserted'.
     *
     * If 'value' has been associated with 'node' and there was a record previously associated with 'node':
     * retains 'value', releases the previous record, and returns 'kTrieResultReplaced'.
     *
     * If 'value' has not beed associated with 'node' because there was a race (i.e., someone else associated a
     * different record first), it does nothing and returns 'kTrieResultRace'.  The caller should decide whether to
     * retry or accept the existing outcome.
     *
     * @result kTrieResultInserted, kTrieResultReplaced, kTrieResultRace, or kTrieResultFailure
     */
    TrieResult replace(Node *node, const OSObject *value);

    /*!
     * Attempts to associate 'value' with 'node', ONLY if no value is already associated with 'node'.
     *
     * If either 'node' or 'value' is NULL, the result is 'kTrieResultFailure'.
     *
     * @result kTrieResultInserted, kTrieResultAlreadyExists, or kTrieResultFailure
     */
    TrieResult insert(Node *node, const OSObject *value);

    /*!
     * Attempts to remove any record currently associated with 'node'.
     *
     * If 'node' is NULL, returns 'kTrieResultFailure'.
     *
     * If no record is currently associated with 'node', returns 'kTrieResultAlreadyEmpty'.
     *
     * If there is a record currently associated with 'node', it releases that record, decrements the size, and
     * returns 'kTrieResultRemoved'.
     *
     * If there is a record currently associated with 'node' but it gets changed in the middle of this method,
     * this method simply returns 'kTrieResultRace'; the caller should decide whether to retry the operation or accept
     * the existing outcome.
     */
    TrieResult remove(Node *node);

    /*! Calls 'callback' for every node in the trie during a pre-order traversal. */
    void traverse(bool computeKey, void *callbackArgs, traverse_fn callback);

    /*!
     * Traverses the trie until it gets to the node corresponding to the given 'key', creating new nodes as necessary.
     * Returning NULL indicates that the system is out of memory.
     */
    Node* findUintNode(uint64_t key);

    /*!
     * Traverses the trie until it gets to the node corresponding to the given 'key', creating new nodes as necessary.
     * Returning NULL indicates that either the key is invalid (e.g., contains non-ascii characters) or the system is out of memory.
     */
    Node* findPathNode(const char *key);

    /*! Creates either a Uint or a Path node, based on the kind of this trie. */
    Node* createNode()
    {
        return kind_ == kUintTrie ? Node::createUintNode() :
               kind_ == kPathTrie ? Node::createPathNode() :
               nullptr;
    }

    /*!
     * Static factory method.  The caller is responsible for releasing it by calling 'release()'.
     */
    static Trie* create(TrieKind kind);

protected:
    void free() override;

public:

    /*!
     * Returns the size of the tree (i.e., the number of values stored).
     */
    inline uint getCount() { return size_; }

    /*!
     * Callback to be invoked every time the size of this tree changes.
     */
    bool onChange(void *callbackArgs, on_change_fn callback);

    /*!
     * Invokes a given callback for every entry in this dictionary.
     *
     * @param callbackArgs Arbitrary pointer passed to 'callback'
     * @param callback Callback function to call for each entry in this dictionary
     */
    void forEach(void *callbackArgs, for_each_fn callback);

    /*!
     * Removes all the entries matching a given filter.
     */
    void removeMatching(void *filterArgs, filter_fn filter);

#pragma mark Methods for 'path' keys

    OSObject* get(const char *path)
    {
        if (kind_ != kPathTrie) return nullptr;
        return get(findPathNode(path));
    }

    template<typename T>
    T* getAs(const char *key)
    {
        if (kind_ != kPathTrie) return nullptr;
        return OSDynamicCast(T, get(key));
    }

    /*!
     * If 'path' hasn't been seen before: creates a new value (using the supplied factory function),
     * associates it with 'path', and returns it; otherwise, returns the 'OSObject' object previously
     * associated with 'path'.
     *
     * Paths are considered case-insensitive.
     *
     * NOTE: The current implementation only paths containig only ASCII characters; for all other paths
     *       nullptr is returned indicating that the path couldn't be added.
     */
    OSObject* getOrAdd(const char *path, void *factoryArgs, factory_fn factory, TrieResult *result = nullptr)
    {
        if (kind_ != kPathTrie) return nullptr;
        return getOrAdd(findPathNode(path), factoryArgs, factory, result);
    }

    TrieResult replace(const char *path, const OSObject *value)
    {
        if (kind_ != kPathTrie) return kTrieResultFailure;
        return replace(findPathNode(path), value);
    }

    TrieResult insert(const char *path, const OSObject *value)
    {
        if (kind_ != kPathTrie) return kTrieResultFailure;
        return insert(findPathNode(path), value);
    }

    TrieResult remove(const char *key)
    {
        if (kind_ != kPathTrie) return kTrieResultFailure;
        return remove(findPathNode(key));
    }

#pragma mark Methods for 'uint' keys

    OSObject* get(uint64_t key)
    {
        if (kind_ != kUintTrie) return nullptr;
        return get(findUintNode(key));
    }

    template<typename T>
    T* getAs(uint64_t key)
    {
        if (kind_ != kUintTrie) return nullptr;
        return OSDynamicCast(T, get(key));
    }

    OSObject* getOrAdd(uint64_t key, void *factoryArgs, factory_fn factory, TrieResult *result = nullptr)
    {
        if (kind_ != kUintTrie) return nullptr;
        return getOrAdd(findUintNode(key), factoryArgs, factory, result);
    }

    TrieResult replace(uint64_t key, const OSObject *value)
    {
        if (kind_ != kUintTrie) return kTrieResultFailure;
        return replace(findUintNode(key), value);
    }

    TrieResult insert(uint64_t key, const OSObject *value)
    {
        if (kind_ != kUintTrie) return kTrieResultFailure;
        return insert(findUintNode(key), value);
    }

    TrieResult remove(uint64_t key)
    {
        if (kind_ != kUintTrie) return kTrieResultFailure;
        return remove(findUintNode(key));
    }

#pragma mark Static factory methods

    static Trie* createUintTrie() { return create(kUintTrie); }
    static Trie* createPathTrie() { return create(kPathTrie); }
};

#endif /* PathCache_hpp */
