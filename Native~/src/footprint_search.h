#pragma once
#include <algorithm>
#include <cstdint>
#include <memory>
#include <vector>

namespace meshlab {
struct FootprintOccupancy {
    int side;
    std::vector<int> offsets, occupied;
    FootprintOccupancy(int side, const int* offsets, const int* occupied, int occupiedInts)
        : side(side), offsets(offsets, offsets + side + 1), occupied(occupied, occupied + occupiedInts) {}
    static bool valid(int side, const int* offsets, const int* occupied, int occupiedInts) {
        if (side < 1 || side > 8192 || !offsets || !occupied || occupiedInts < 0 ||
            occupiedInts > 20000000 || occupiedInts % 2 || offsets[0] != 0 || offsets[side] != occupiedInts) return false;
        for (int row = 0; row < side; ++row) {
            if (offsets[row] < 0 || offsets[row] > offsets[row + 1] || offsets[row + 1] > occupiedInts ||
                offsets[row] % 2 || offsets[row + 1] % 2) return false;
            int end = 0;
            for (int i = offsets[row]; i < offsets[row + 1]; i += 2) {
                if (occupied[i] < end || occupied[i] >= occupied[i + 1] || occupied[i + 1] > side) return false;
                end = occupied[i + 1];
            }
        }
        return true;
    }
};
// All intervals are half-open. Span traversal and collision jumps match the
// managed reference; chunks limit actual work without changing its work cap.
class FootprintSearch {
    struct Cursor { int y = -1, threshold = 0, index = 0; };
    std::vector<int> spans_;
    std::shared_ptr<const FootprintOccupancy> occupancy_;
    std::vector<Cursor> cursors_;
    int side_, width_, height_;
    size_t resumeSpan_ = 0;
    int resumeX_ = -1, resumeY_ = -1;
public:
    FootprintSearch(int side, int width, int height, const int* spans, int spanInts,
        const int* offsets, const int* occupied, int occupiedInts)
        : FootprintSearch(std::make_shared<FootprintOccupancy>(side, offsets, occupied, occupiedInts),
            width, height, spans, spanInts) {}

    FootprintSearch(std::shared_ptr<const FootprintOccupancy> occupancy, int width, int height,
        const int* spans, int spanInts)
        : spans_(spans, spans + spanInts), occupancy_(std::move(occupancy)), cursors_(height),
          side_(occupancy_->side), width_(width), height_(height) {}

    static bool valid(int side, int width, int height, const int* spans, int spanInts,
        const int* offsets, const int* occupied, int occupiedInts) {
        return validMask(side, width, height, spans, spanInts) &&
            FootprintOccupancy::valid(side, offsets, occupied, occupiedInts);
    }

    static bool validMask(int side, int width, int height, const int* spans, int spanInts) {
        if (side < 1 || side > 8192 || width < 1 || width > side || height < 1 || height > side ||
            !spans || spanInts < 0 || spanInts > 20000000 || spanInts % 3) return false;
        int previousRow = -1, previousEnd = 0;
        for (int s = 0; s < spanInts; s += 3) {
            int row = spans[s], start = spans[s + 1], end = spans[s + 2];
            if (row < 0 || row < previousRow || row >= height || start < 0 || start >= end || end > width ||
                (row == previousRow && start < previousEnd)) return false;
            previousRow = row; previousEnd = end;
        }
        return true;
    }

    // 1: free candidate, 0: exhausted, 2: chunk finished, -1: logical cap,
    // -2: invalid call. x/y are the next reference candidate on a chunk yield.
    int advance(int& x, int& y, int best, int64_t& work, int chunk) {
        if (x < 0 || x > side_ + 1 || y < 0 || y > side_ + 1 || work < 0 || work == INT64_MAX || chunk < 1) return -2;
        const auto& offsets_ = occupancy_->offsets;
        const auto& occupied_ = occupancy_->occupied;
        int64_t actual = 0;
        if (resumeX_ != x || resumeY_ != y) resumeSpan_ = 0;
        while (y + height_ <= side_ && y + height_ < best) {
            while (x + width_ <= side_) {
                int next = 0;
                size_t firstSpan = resumeSpan_; resumeSpan_ = 0;
                for (size_t s = firstSpan; s < spans_.size(); s += 3) {
                    if (++work > 128000000) return -1;
                    ++actual;
                    int row = spans_[s], start = x + spans_[s + 1], end = x + spans_[s + 2];
                    int first = offsets_[y + row], count = (offsets_[y + row + 1] - first) / 2;
                    Cursor& cursor = cursors_[row];
                    int lo = cursor.y == y && start >= cursor.threshold ? cursor.index : 0;
                    cursor.y = y; cursor.threshold = start;
                    if (lo < count && occupied_[first + lo * 2 + 1] <= start) {
                        ++lo;
                        if (lo < count && occupied_[first + lo * 2 + 1] <= start) {
                            int hi = count;
                            while (lo < hi) {
                                int middle = lo + (hi - lo) / 2;
                                if (occupied_[first + middle * 2 + 1] <= start) lo = middle + 1;
                                else hi = middle;
                            }
                        }
                    }
                    cursor.index = lo;
                    if (lo < count && occupied_[first + lo * 2] < end) {
                        next = std::max(x + 1, occupied_[first + lo * 2 + 1] - spans_[s + 1]);
                        break;
                    }
                    if (actual >= chunk && s + 3 < spans_.size()) {
                        resumeSpan_ = s + 3; resumeX_ = x; resumeY_ = y; return 2;
                    }
                }
                if (!next) {
                    if (std::max(x + width_, y + height_) < best) return 1;
                    break;
                }
                x = next;
                if (actual >= chunk) return 2;
            }
            x = 0; ++y;
            // Empty masks still yield between rows for cancellation.
            if (actual >= chunk || spans_.empty()) return 2;
        }
        return 0;
    }
};
}
