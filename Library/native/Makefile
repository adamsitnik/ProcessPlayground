CC = gcc
CFLAGS = -Wall -O2 -fPIC
LDFLAGS = -shared -lpthread

# Detect platform and set library extension
UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Darwin)
    TARGET = libprocessspawn.dylib
else
    TARGET = libprocessspawn.so
endif

SOURCES = process_spawn.c
OBJECTS = $(SOURCES:.c=.o)

all: $(TARGET)

$(TARGET): $(OBJECTS)
	$(CC) $(LDFLAGS) -o $@ $^

%.o: %.c
	$(CC) $(CFLAGS) -c $< -o $@

clean:
	rm -f $(OBJECTS) $(TARGET)

.PHONY: all clean