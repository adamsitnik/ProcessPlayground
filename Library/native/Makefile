CC = gcc
CFLAGS = -Wall -O2 -fPIC
LDFLAGS = -shared -lpthread

TARGET = libprocessspawn.so
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