#include <dlfcn.h>
#include <stdio.h>
#include <string.h>

typedef void *(*CreateClient)(void);
typedef const char *(*Execute)(void *, const char *);
typedef void (*Send)(void *, const char *);
typedef const char *(*Receive)(void *, double);
typedef void (*Destroy)(void *);

static int await_message(void *client, Receive receive, const char *type, const char *extra)
{
    for (int attempt = 0; attempt < 20; ++attempt) {
        const char *message = receive(client, 0.25);
        if (message && strstr(message, type) && (!extra || strstr(message, extra))) {
            return 1;
        }
    }

    return 0;
}

int main(int argc, char **argv)
{
    if (argc != 2) {
        fputs("Usage: tdlib-smoke /absolute/path/to/libtdjson.so\n", stderr);
        return 2;
    }

    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) {
        fprintf(stderr, "%s\n", dlerror());
        return 1;
    }

    CreateClient create = (CreateClient)dlsym(library, "td_json_client_create");
    Execute execute = (Execute)dlsym(library, "td_json_client_execute");
    Send send = (Send)dlsym(library, "td_json_client_send");
    Receive receive = (Receive)dlsym(library, "td_json_client_receive");
    Destroy destroy = (Destroy)dlsym(library, "td_json_client_destroy");
    if (!create || !execute || !send || !receive || !destroy) {
        fputs("The TDLib JSON ABI is incomplete.\n", stderr);
        dlclose(library);
        return 1;
    }

    execute(NULL, "{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}");
    void *client = create();
    if (!client) {
        dlclose(library);
        return 1;
    }

    // No initialization parameters, credentials, or Telegram network commands are sent.
    send(client, "{\"@type\":\"getAuthorizationState\",\"@extra\":\"offline-smoke\"}");
    int initialized = await_message(client, receive,
        "authorizationStateWaitTdlibParameters", "offline-smoke");
    send(client, "{\"@type\":\"close\"}");
    int closed = await_message(client, receive, "authorizationStateClosed", NULL);
    destroy(client);

    if (!initialized || !closed) {
        fputs("The offline TDLib authorization-state/close check failed.\n", stderr);
        return 1;
    }

    puts("TDLib offline JSON ABI smoke test passed.");
    return 0;
}
