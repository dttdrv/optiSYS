const std = @import("std");
const app = @import("app/app.zig");

pub fn main() !void {
    var gpa = std.heap.GeneralPurposeAllocator(.{}){};
    defer {
        const leaked = gpa.deinit();
        if (leaked == .leak) {
            std.log.err("memory leak detected during shutdown", .{});
        }
    }

    const allocator = gpa.allocator();
    try app.run(allocator);
}
