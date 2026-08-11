/*
 * Arduino Mouse Controller
 *
 * This Arduino sketch receives commands via serial (COM port) and
 * executes mouse movements and clicks using the Mouse library.
 *
 * Compatible with: Arduino Leonardo, Micro, or any 32u4-based board
 *
 * Protocol: COMMAND:X:Y\n
 * Commands:
 *   MOVE:x:y              - Move mouse relatively by x,y pixels
 *   LEFTCLICK:0:0         - Perform left mouse click
 *   DOUBLELEFTCLICK:0:0   - Perform double left click
 *   RIGHTCLICK:0:0        - Perform right mouse click
 *   LEFTDOWN:0:0          - Press left mouse button (hold)
 *   LEFTUP:0:0            - Release left mouse button
 *   RIGHTDOWN:0:0         - Press right mouse button (hold)
 *   RIGHTUP:0:0           - Release right mouse button
 */

#include <Mouse.h>

// Configuration
const long BAUD_RATE = 115200;
const int MAX_COMMAND_LENGTH = 64;

// Command buffer
char commandBuffer[MAX_COMMAND_LENGTH];
int bufferIndex = 0;

void setup() {
  // Initialize serial communication
  Serial.begin(BAUD_RATE);

  // Initialize mouse control
  Mouse.begin();

  // Wait for serial port to connect
  while (!Serial) {
    ; // wait for serial port to connect
  }

  // Send ready signal
  Serial.println("ARDUINO_MOUSE_READY");
}

void loop() {
  // Check if data is available on serial port
  if (Serial.available() > 0) {
    char inChar = Serial.read();

    // Check for newline (end of command)
    if (inChar == '\n' || inChar == '\r') {
      if (bufferIndex > 0) {
        commandBuffer[bufferIndex] = '\0';  // Null-terminate string
        processCommand(commandBuffer);
        bufferIndex = 0;  // Reset buffer
      }
    } else if (bufferIndex < MAX_COMMAND_LENGTH - 1) {
      commandBuffer[bufferIndex++] = inChar;
    }
  }
}

void processCommand(char* command) {
  // Parse command string
  char* token = strtok(command, ":");
  if (token == NULL) return;

  String cmd = String(token);
  cmd.toUpperCase();

  // Parse X and Y parameters (if present)
  int x = 0, y = 0;
  token = strtok(NULL, ":");
  if (token != NULL) x = atoi(token);
  token = strtok(NULL, ":");
  if (token != NULL) y = atoi(token);

  // Execute command
  if (cmd == "MOVE") {
    Mouse.move(x, y, 0);
    Serial.println("OK:MOVE");
  }
  else if (cmd == "LEFTCLICK") {
    Mouse.click(MOUSE_LEFT);
    Serial.println("OK:LEFTCLICK");
  }
  else if (cmd == "DOUBLELEFTCLICK") {
    Mouse.click(MOUSE_LEFT);
    delay(50);
    Mouse.click(MOUSE_LEFT);
    Serial.println("OK:DOUBLELEFTCLICK");
  }
  else if (cmd == "RIGHTCLICK") {
    Mouse.click(MOUSE_RIGHT);
    Serial.println("OK:RIGHTCLICK");
  }
  else if (cmd == "LEFTDOWN") {
    Mouse.press(MOUSE_LEFT);
    Serial.println("OK:LEFTDOWN");
  }
  else if (cmd == "LEFTUP") {
    Mouse.release(MOUSE_LEFT);
    Serial.println("OK:LEFTUP");
  }
  else if (cmd == "RIGHTDOWN") {
    Mouse.press(MOUSE_RIGHT);
    Serial.println("OK:RIGHTDOWN");
  }
  else if (cmd == "RIGHTUP") {
    Mouse.release(MOUSE_RIGHT);
    Serial.println("OK:RIGHTUP");
  }
  else {
    Serial.println("ERROR:UNKNOWN_COMMAND");
  }
}
