#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static int interrupted;
static int interrupt_armed;
static int flush_interrupted;

void arm_interrupt(void)
{
    interrupt_armed = 1;
}

static void fd_name(int fd, char *target, size_t size)
{
    char link[64];
    snprintf(link, sizeof(link), "/proc/self/fd/%d", fd);
    ssize_t length = readlink(link, target, size - 1);
    target[length < 0 ? 0 : length] = 0;
}

static void observe(const char *event)
{
    const char *log = getenv("HEXALITH_NATIVE_PROBE_LOG");
    if (!log) return;
    FILE *file = fopen(log, "a");
    if (!file) return;
    fprintf(file, "%s\n", event);
    fclose(file);
}

int flock(int fd, int operation)
{
    int (*actual)(int, int) = dlsym(RTLD_NEXT, "flock");
    char path[4096];
    fd_name(fd, path, sizeof(path));
    if (interrupt_armed && operation == 5 && strstr(path, "/metadata-read.json"))
    {
        if (__sync_bool_compare_and_swap(&interrupted, 0, 1))
        {
            observe("injected-EINTR");
            errno = EINTR;
            return -1;
        }
        int result = actual(fd, operation);
        if (result == 0) observe("shared-lock-ok");
        return result;
    }
    return actual(fd, operation);
}

int fsync(int fd)
{
    int (*actual)(int) = dlsym(RTLD_NEXT, "fsync");
    char path[4096];
    fd_name(fd, path, sizeof(path));
    const char *mode = getenv("HEXALITH_NATIVE_PROBE_MODE");
    if (mode && strstr(path, "/.metadata-read.json.")
        && (strcmp(mode, "document-flush-eintr") == 0 || strcmp(mode, "document-flush-cancelled") == 0))
    {
        int first = __sync_bool_compare_and_swap(&flush_interrupted, 0, 1);
        if (first) observe("injected-flush-EINTR");
        if (first || strcmp(mode, "document-flush-cancelled") == 0)
        {
            errno = EINTR;
            return -1;
        }
        int result = actual(fd);
        if (result == 0) observe("durable-flush-ok");
        return result;
    }

    if (mode && ((strcmp(mode, "document-flush") == 0 && strstr(path, "/.metadata-read.json."))
        || (strcmp(mode, "snapshot-flush") == 0 && strstr(path, "/.archive-"))))
    {
        observe("injected-flush-failure");
        errno = EIO;
        return -1;
    }
    return actual(fd);
}
