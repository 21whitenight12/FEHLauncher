#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
MO2 AI Assistant - Gemini-powered Mod Organizer 2 Control
===========================================================
Single-file, model-agnostic AI assistant for managing MO2 via natural language.

Requirements:
    pip install google-generativeai

Usage:
    python mo2_ai_assistant.py
    
Environment Variables:
    GEMINI_API_KEY - Your Google Gemini API key
    MO2_BRIDGE_HOST - Bridge host (default: 127.0.0.1)
    MO2_BRIDGE_PORT - Bridge port (default: 52525)

Author: AI Bridge Team
Version: 1.0.1
"""

import os
import sys
import socket
import json
import uuid
import time
import threading
from typing import Any, Dict, List, Optional, Tuple
from abc import ABC, abstractmethod
from dataclasses import dataclass


# =============================================================================
# CONFIGURATION
# =============================================================================

@dataclass
class Config:
    """Application configuration."""
    bridge_host: str = "127.0.0.1"
    bridge_port: int = 52525
    bridge_timeout: float = 30.0
    gemini_api_key: str = ""
    gemini_model: str = "gemini-2.5-flash"
    use_colors: bool = True
    show_tool_calls: bool = True
    
    @classmethod
    def from_env(cls) -> 'Config':
        config = cls()
        config.bridge_host = os.environ.get('MO2_BRIDGE_HOST', config.bridge_host)
        config.bridge_port = int(os.environ.get('MO2_BRIDGE_PORT', str(config.bridge_port)))
        config.gemini_api_key = os.environ.get('GEMINI_API_KEY', '')
        return config


# =============================================================================
# CONSOLE UTILITIES
# =============================================================================

class Console:
    """Colored console output utilities."""
    
    COLORS = {
        'reset': '\033[0m',
        'bold': '\033[1m',
        'dim': '\033[2m',
        'red': '\033[91m',
        'green': '\033[92m',
        'yellow': '\033[93m',
        'blue': '\033[94m',
        'magenta': '\033[95m',
        'cyan': '\033[96m',
    }
    
    def __init__(self, use_colors: bool = True):
        self.use_colors = use_colors and self._supports_color()
    
    @staticmethod
    def _supports_color() -> bool:
        if sys.platform == 'win32':
            try:
                import ctypes
                kernel32 = ctypes.windll.kernel32
                kernel32.SetConsoleMode(kernel32.GetStdHandle(-11), 7)
                return True
            except:
                return os.environ.get('TERM') is not None
        return hasattr(sys.stdout, 'isatty') and sys.stdout.isatty()
    
    def _color(self, text: str, *colors: str) -> str:
        if not self.use_colors:
            return text
        prefix = ''.join(self.COLORS.get(c, '') for c in colors)
        return f"{prefix}{text}{self.COLORS['reset']}"
    
    def info(self, msg: str):
        print(self._color(f"ℹ {msg}", 'cyan'))
    
    def success(self, msg: str):
        print(self._color(f"✓ {msg}", 'green'))
    
    def warning(self, msg: str):
        print(self._color(f"⚠ {msg}", 'yellow'))
    
    def error(self, msg: str):
        print(self._color(f"✗ {msg}", 'red'))
    
    def tool(self, msg: str):
        print(self._color(f"🔧 {msg}", 'magenta'))
    
    def ai(self, msg: str):
        print(self._color(f"🤖 {msg}", 'blue'))
    
    def user_prompt(self) -> str:
        return self._color("You: ", 'green', 'bold')
    
    def ai_prompt(self) -> str:
        return self._color("AI: ", 'blue', 'bold')
    
    def divider(self):
        print(self._color("─" * 60, 'dim'))


console = Console()


# =============================================================================
# MO2 BRIDGE CLIENT
# =============================================================================

class MO2BridgeClient:
    """Client for communicating with MO2 AI Bridge."""
    
    DELIMITER = b'\n\x00\x00\n'
    
    def __init__(self, host: str = "127.0.0.1", port: int = 52525, timeout: float = 30.0):
        self.host = host
        self.port = port
        self.timeout = timeout
        
        self._socket: Optional[socket.socket] = None
        self._client_id: Optional[str] = None
        self._connected = False
        self._running = False
        
        self._lock = threading.Lock()
        self._condition = threading.Condition(self._lock)
        self._pending: Dict[str, dict] = {}
        self._recv_thread: Optional[threading.Thread] = None
        
        self.available_methods: List[str] = []
    
    @property
    def connected(self) -> bool:
        return self._connected
    
    def connect(self) -> bool:
        try:
            self._socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self._socket.settimeout(self.timeout)
            self._socket.connect((self.host, self.port))
            
            handshake = self._recv_single_message()
            if not handshake or handshake.get('type') != 'handshake':
                raise ConnectionError("Invalid server handshake")
            
            result = handshake.get('result', {})
            self._client_id = result.get('client_id')
            self.available_methods = result.get('available_methods', [])
            
            self._send({
                'type': 'handshake',
                'id': str(uuid.uuid4()),
                'kwargs': {
                    'name': 'MO2 AI Assistant',
                    'subscribe_events': ['mod_list_changed', 'profile_changed']
                }
            })
            
            self._connected = True
            self._running = True
            
            self._recv_thread = threading.Thread(target=self._receive_loop, daemon=True)
            self._recv_thread.start()
            
            return True
            
        except Exception as e:
            console.error(f"Connection failed: {e}")
            return False
    
    def disconnect(self):
        self._running = False
        self._connected = False
        
        if self._socket:
            try:
                self._socket.close()
            except:
                pass
            self._socket = None
        
        with self._condition:
            self._condition.notify_all()
    
    def call(self, method: str, *args, **kwargs) -> Any:
        if not self._connected:
            raise ConnectionError("Not connected to MO2 Bridge")
        
        request_id = str(uuid.uuid4())
        
        with self._condition:
            self._pending[request_id] = {'done': False, 'result': None, 'error': None}
        
        try:
            self._send({
                'type': 'request',
                'id': request_id,
                'method': method,
                'args': list(args),
                'kwargs': kwargs
            })
            
            with self._condition:
                deadline = time.time() + self.timeout
                while not self._pending[request_id]['done']:
                    remaining = deadline - time.time()
                    if remaining <= 0:
                        raise TimeoutError(f"Call to '{method}' timed out")
                    self._condition.wait(timeout=remaining)
                
                data = self._pending[request_id]
            
            if data['error']:
                raise Exception(data['error'])
            
            return data['result']
            
        finally:
            with self._condition:
                self._pending.pop(request_id, None)
    
    def _send(self, msg: dict):
        data = json.dumps(msg, default=str).encode('utf-8') + self.DELIMITER
        self._socket.sendall(data)
    
    def _recv_single_message(self) -> Optional[dict]:
        buffer = b""
        while self.DELIMITER not in buffer:
            chunk = self._socket.recv(65536)
            if not chunk:
                return None
            buffer += chunk
        
        msg_data, _ = buffer.split(self.DELIMITER, 1)
        return json.loads(msg_data.decode('utf-8'))
    
    def _receive_loop(self):
        buffer = b""
        self._socket.settimeout(5.0)
        
        while self._running:
            try:
                data = self._socket.recv(65536)
                if not data:
                    break
                
                buffer += data
                
                while self.DELIMITER in buffer:
                    msg_data, buffer = buffer.split(self.DELIMITER, 1)
                    try:
                        msg = json.loads(msg_data.decode('utf-8'))
                        self._handle_message(msg)
                    except json.JSONDecodeError:
                        pass
                
                if len(buffer) > 1024 * 1024:
                    buffer = b""
                    
            except socket.timeout:
                continue
            except Exception as e:
                if self._running:
                    console.error(f"Receive error: {e}")
                break
        
        self._connected = False
    
    def _handle_message(self, msg: dict):
        msg_type = msg.get('type')
        
        if msg_type == 'response':
            request_id = msg.get('id')
            with self._condition:
                if request_id in self._pending:
                    self._pending[request_id] = {
                        'done': True,
                        'result': msg.get('result'),
                        'error': msg.get('error')
                    }
                    self._condition.notify_all()
        
        elif msg_type == 'heartbeat':
            try:
                self._send({'type': 'heartbeat', 'id': str(uuid.uuid4())})
            except:
                pass


# =============================================================================
# MO2 TOOLS
# =============================================================================

class MO2Tools:
    """MO2 tool implementations that AI can call."""
    
    def __init__(self, client: MO2BridgeClient):
        self.client = client
        self._mod_cache: Optional[List[str]] = None
        self._cache_time: float = 0
        self._cache_ttl: float = 30.0
    
    def _get_all_mods_cached(self) -> List[str]:
        if self._mod_cache is None or (time.time() - self._cache_time) > self._cache_ttl:
            self._mod_cache = self.client.call('modList.allMods')
            self._cache_time = time.time()
        return self._mod_cache
    
    def _invalidate_cache(self):
        self._mod_cache = None
    
    def _find_mod(self, query: str) -> Optional[str]:
        mods = self._get_all_mods_cached()
        query_lower = query.lower()
        
        for mod in mods:
            if mod.lower() == query_lower:
                return mod
        
        matches = [m for m in mods if query_lower in m.lower()]
        if len(matches) == 1:
            return matches[0]
        
        return None
    
    def get_all_mods(self) -> Dict[str, Any]:
        try:
            mods = self.client.call('modList.allMods')
            self._mod_cache = mods
            self._cache_time = time.time()
            return {"success": True, "count": len(mods), "mods": mods}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def get_mod_info(self, mod_name: str) -> Dict[str, Any]:
        try:
            actual_name = self._find_mod(mod_name)
            if not actual_name:
                mods = self._get_all_mods_cached()
                similar = [m for m in mods if mod_name.lower() in m.lower()][:5]
                return {"success": False, "error": f"Mod '{mod_name}' not found", "similar_mods": similar}
            
            state = self.client.call('modList.state', actual_name)
            priority = self.client.call('modList.priority', actual_name)
            state_text = {0: "missing", 1: "disabled", 2: "enabled"}.get(state, "unknown")
            
            return {
                "success": True,
                "name": actual_name,
                "state": state_text,
                "state_code": state,
                "priority": priority,
                "is_active": state == 2
            }
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def enable_mod(self, mod_name: str) -> Dict[str, Any]:
        try:
            actual_name = self._find_mod(mod_name)
            if not actual_name:
                mods = self._get_all_mods_cached()
                similar = [m for m in mods if mod_name.lower() in m.lower()][:5]
                return {"success": False, "error": f"Mod '{mod_name}' not found", "similar_mods": similar}
            
            self.client.call('modList.setActive', actual_name, True)
            self._invalidate_cache()
            return {"success": True, "mod": actual_name, "action": "enabled"}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def disable_mod(self, mod_name: str) -> Dict[str, Any]:
        try:
            actual_name = self._find_mod(mod_name)
            if not actual_name:
                mods = self._get_all_mods_cached()
                similar = [m for m in mods if mod_name.lower() in m.lower()][:5]
                return {"success": False, "error": f"Mod '{mod_name}' not found", "similar_mods": similar}
            
            self.client.call('modList.setActive', actual_name, False)
            self._invalidate_cache()
            return {"success": True, "mod": actual_name, "action": "disabled"}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def set_mod_priority(self, mod_name: str, priority: int) -> Dict[str, Any]:
        try:
            actual_name = self._find_mod(mod_name)
            if not actual_name:
                return {"success": False, "error": f"Mod '{mod_name}' not found"}
            
            old_priority = self.client.call('modList.priority', actual_name)
            self.client.call('modList.setPriority', actual_name, priority)
            new_priority = self.client.call('modList.priority', actual_name)
            
            return {
                "success": True,
                "mod": actual_name,
                "old_priority": old_priority,
                "new_priority": new_priority
            }
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def get_active_mods(self) -> Dict[str, Any]:
        try:
            mods = self._get_all_mods_cached()
            active = []
            
            for mod in mods:
                try:
                    state = self.client.call('modList.state', mod)
                    if state == 2:
                        active.append(mod)
                except:
                    pass
            
            return {"success": True, "count": len(active), "total_mods": len(mods), "active_mods": active}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def get_plugins(self) -> Dict[str, Any]:
        try:
            plugins = self.client.call('pluginList.pluginNames')
            return {"success": True, "count": len(plugins), "plugins": plugins}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def get_plugin_info(self, plugin_name: str) -> Dict[str, Any]:
        try:
            state = self.client.call('pluginList.state', plugin_name)
            priority = self.client.call('pluginList.priority', plugin_name)
            load_order = self.client.call('pluginList.loadOrder', plugin_name)
            state_text = {0: "missing", 1: "disabled", 2: "enabled"}.get(state, "unknown")
            
            return {
                "success": True,
                "name": plugin_name,
                "state": state_text,
                "priority": priority,
                "load_order": load_order
            }
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def enable_plugin(self, plugin_name: str) -> Dict[str, Any]:
        try:
            self.client.call('pluginList.setState', plugin_name, 2)
            return {"success": True, "plugin": plugin_name, "action": "enabled"}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def disable_plugin(self, plugin_name: str) -> Dict[str, Any]:
        try:
            self.client.call('pluginList.setState', plugin_name, 1)
            return {"success": True, "plugin": plugin_name, "action": "disabled"}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def get_profile(self) -> Dict[str, Any]:
        try:
            name = self.client.call('organizer.profileName')
            path = self.client.call('organizer.profilePath')
            mods_path = self.client.call('organizer.modsPath')
            
            return {
                "success": True,
                "profile_name": name,
                "profile_path": path,
                "mods_path": mods_path
            }
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def search_mods(self, query: str) -> Dict[str, Any]:
        try:
            mods = self._get_all_mods_cached()
            query_lower = query.lower()
            
            matches = []
            for mod in mods:
                if query_lower in mod.lower():
                    try:
                        state = self.client.call('modList.state', mod)
                        state_text = {0: "missing", 1: "disabled", 2: "enabled"}.get(state, "unknown")
                        matches.append({"name": mod, "state": state_text})
                    except:
                        matches.append({"name": mod, "state": "unknown"})
            
            return {"success": True, "query": query, "count": len(matches), "matches": matches}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def refresh_modlist(self) -> Dict[str, Any]:
        try:
            self.client.call('organizer.refreshModList', False)
            self._invalidate_cache()
            return {"success": True, "action": "refreshed"}
        except Exception as e:
            return {"success": False, "error": str(e)}


# =============================================================================
# GEMINI PROVIDER (FIXED)
# =============================================================================

class GeminiProvider:
    """Google Gemini AI provider with function calling support."""
    
    SYSTEM_PROMPT = """You are an AI assistant for Mod Organizer 2 (MO2). You help users manage their game mods through natural language commands.

Your capabilities:
- List, search, enable, and disable mods
- Manage mod load order (priority)
- Control ESP/ESM/ESL plugins
- Provide information about the current mod setup

Guidelines:
1. When asked about mods, use the appropriate tools to get real data
2. For enabling/disabling, always confirm the action was successful
3. If a mod name is ambiguous or not found, show similar mods
4. Be helpful and explain what you're doing
5. Keep responses concise but informative

Common user intents:
- "list mods", "show mods", "what mods" → use get_all_mods or get_active_mods
- "enable X", "turn on X" → use enable_mod
- "disable X", "turn off X" → use disable_mod
- "find X", "search X" → use search_mods
- "info about X" → use get_mod_info"""

    def __init__(self, api_key: str, model: str = "gemini-1.5-flash"):
        self.api_key = api_key
        self.model_name = model
        self.model = None
        self.chat_session = None
        self._pending_tool_results = []
    
    def initialize(self) -> bool:
        """Initialize Gemini API."""
        try:
            import google.generativeai as genai
            
            genai.configure(api_key=self.api_key)
            
            # Define tools using the simpler dictionary format
            tools = {
                "function_declarations": [
                    {
                        "name": "get_all_mods",
                        "description": "Get a list of all installed mod names in MO2",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {}
                        }
                    },
                    {
                        "name": "get_mod_info",
                        "description": "Get detailed information about a specific mod including its state and priority",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {
                                "mod_name": {
                                    "type": "STRING",
                                    "description": "The name of the mod"
                                }
                            },
                            "required": ["mod_name"]
                        }
                    },
                    {
                        "name": "enable_mod",
                        "description": "Enable/activate a mod so it will be loaded by the game",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {
                                "mod_name": {
                                    "type": "STRING",
                                    "description": "The name of the mod to enable"
                                }
                            },
                            "required": ["mod_name"]
                        }
                    },
                    {
                        "name": "disable_mod",
                        "description": "Disable/deactivate a mod so it won't be loaded",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {
                                "mod_name": {
                                    "type": "STRING",
                                    "description": "The name of the mod to disable"
                                }
                            },
                            "required": ["mod_name"]
                        }
                    },
                    {
                        "name": "set_mod_priority",
                        "description": "Set the load priority of a mod (lower = loads earlier)",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {
                                "mod_name": {
                                    "type": "STRING",
                                    "description": "The name of the mod"
                                },
                                "priority": {
                                    "type": "INTEGER",
                                    "description": "The new priority value"
                                }
                            },
                            "required": ["mod_name", "priority"]
                        }
                    },
                    {
                        "name": "get_active_mods",
                        "description": "Get a list of currently enabled/active mods",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {}
                        }
                    },
                    {
                        "name": "get_plugins",
                        "description": "Get a list of all ESP/ESM/ESL plugin files",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {}
                        }
                    },
                    {
                        "name": "get_plugin_info",
                        "description": "Get information about a specific plugin file",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {
                                "plugin_name": {
                                    "type": "STRING",
                                    "description": "The plugin filename (e.g., 'SkyUI_SE.esp')"
                                }
                            },
                            "required": ["plugin_name"]
                        }
                    },
                    {
                        "name": "enable_plugin",
                        "description": "Enable an ESP/ESM plugin",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {
                                "plugin_name": {
                                    "type": "STRING",
                                    "description": "The plugin filename"
                                }
                            },
                            "required": ["plugin_name"]
                        }
                    },
                    {
                        "name": "disable_plugin",
                        "description": "Disable an ESP/ESM plugin",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {
                                "plugin_name": {
                                    "type": "STRING",
                                    "description": "The plugin filename"
                                }
                            },
                            "required": ["plugin_name"]
                        }
                    },
                    {
                        "name": "get_profile",
                        "description": "Get current MO2 profile information",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {}
                        }
                    },
                    {
                        "name": "search_mods",
                        "description": "Search for mods by name (partial match)",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {
                                "query": {
                                    "type": "STRING",
                                    "description": "Search query"
                                }
                            },
                            "required": ["query"]
                        }
                    },
                    {
                        "name": "refresh_modlist",
                        "description": "Refresh MO2 mod list from disk",
                        "parameters": {
                            "type": "OBJECT",
                            "properties": {}
                        }
                    }
                ]
            }
            
            self.model = genai.GenerativeModel(
                model_name=self.model_name,
                tools=[tools],
                system_instruction=self.SYSTEM_PROMPT
            )
            
            self.chat_session = self.model.start_chat(history=[])
            
            return True
            
        except ImportError:
            console.error("google-generativeai not installed. Run: pip install google-generativeai")
            return False
        except Exception as e:
            console.error(f"Failed to initialize Gemini: {e}")
            return False
    
    def chat(self, message: str) -> Tuple[str, List[dict]]:
        """Send message and get response with potential tool calls."""
        try:
            response = self.chat_session.send_message(message)
            
            tool_calls = []
            text_response = ""
            
            for part in response.parts:
                if hasattr(part, 'function_call') and part.function_call:
                    fc = part.function_call
                    tool_calls.append({
                        "name": fc.name,
                        "arguments": dict(fc.args) if fc.args else {}
                    })
                elif hasattr(part, 'text') and part.text:
                    text_response += part.text
            
            return text_response, tool_calls
            
        except Exception as e:
            console.error(f"Gemini API error: {e}")
            return f"Sorry, I encountered an error: {e}", []
    
    def add_tool_result(self, tool_name: str, result: Any) -> None:
        """Store tool result for later processing."""
        self._pending_tool_results.append({
            "name": tool_name,
            "result": result
        })
    
    def get_final_response(self) -> str:
        """Get final response after processing tool results."""
        if not self._pending_tool_results:
            return ""
        
        try:
            import google.generativeai as genai
            
            parts = []
            for tool_result in self._pending_tool_results:
                parts.append(
                    genai.protos.Part(
                        function_response=genai.protos.FunctionResponse(
                            name=tool_result["name"],
                            response={"result": tool_result["result"]}
                        )
                    )
                )
            
            response = self.chat_session.send_message(parts)
            self._pending_tool_results = []
            
            text = ""
            for part in response.parts:
                if hasattr(part, 'text') and part.text:
                    text += part.text
            
            return text
            
        except Exception as e:
            console.error(f"Error getting final response: {e}")
            self._pending_tool_results = []
            return f"Error processing results: {e}"


# =============================================================================
# MAIN ASSISTANT
# =============================================================================

class MO2Assistant:
    """Main assistant combining AI provider with MO2 tools."""
    
    def __init__(self, config: Config):
        self.config = config
        self.client: Optional[MO2BridgeClient] = None
        self.tools: Optional[MO2Tools] = None
        self.ai: Optional[GeminiProvider] = None
        self.conversation_history: List[dict] = []
    
    def initialize(self) -> bool:
        console.info("Initializing MO2 AI Assistant...")
        
        # Connect to MO2 Bridge
        console.info(f"Connecting to MO2 Bridge at {self.config.bridge_host}:{self.config.bridge_port}...")
        self.client = MO2BridgeClient(
            host=self.config.bridge_host,
            port=self.config.bridge_port,
            timeout=self.config.bridge_timeout
        )
        
        if not self.client.connect():
            console.error("Failed to connect to MO2 Bridge. Is MO2 running?")
            return False
        
        console.success("Connected to MO2 Bridge")
        
        # Initialize tools
        self.tools = MO2Tools(self.client)
        
        # Initialize AI
        console.info("Initializing Gemini AI...")
        self.ai = GeminiProvider(
            api_key=self.config.gemini_api_key,
            model=self.config.gemini_model
        )
        
        if not self.ai.initialize():
            return False
        
        console.success("Gemini AI initialized")
        
        # Show initial info
        try:
            profile = self.client.call('organizer.profileName')
            mod_count = len(self.client.call('modList.allMods'))
            console.info(f"Current profile: {profile} ({mod_count} mods)")
        except Exception as e:
            console.warning(f"Could not get profile info: {e}")
        
        return True
    
    def execute_tool(self, tool_name: str, arguments: dict) -> Any:
        """Execute a tool and return the result."""
        tool_method = getattr(self.tools, tool_name, None)
        
        if tool_method is None:
            return {"success": False, "error": f"Unknown tool: {tool_name}"}
        
        try:
            if self.config.show_tool_calls:
                args_str = ", ".join(f"{k}={v!r}" for k, v in arguments.items())
                console.tool(f"{tool_name}({args_str})")
            
            return tool_method(**arguments)
            
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def process_message(self, user_message: str) -> str:
        """Process a user message and return AI response."""
        
        response_text, tool_calls = self.ai.chat(user_message)
        
        # Execute tool calls if any
        if tool_calls:
            for tool_call in tool_calls:
                tool_name = tool_call["name"]
                arguments = tool_call["arguments"]
                
                result = self.execute_tool(tool_name, arguments)
                self.ai.add_tool_result(tool_name, result)
            
            final_response = self.ai.get_final_response()
            if final_response:
                response_text = final_response
        
        # Update history
        self.conversation_history.append({"role": "user", "content": user_message})
        self.conversation_history.append({"role": "assistant", "content": response_text})
        
        if len(self.conversation_history) > 20:
            self.conversation_history = self.conversation_history[-20:]
        
        return response_text
    
    def run(self):
        """Run the interactive chat loop."""
        console.divider()
        print()
        console.ai("Hello! I'm your MO2 AI Assistant. I can help you manage your mods.")
        console.ai("Try commands like:")
        print("  • 'Show me all mods'")
        print("  • 'Enable SkyUI'")
        print("  • 'Search for texture mods'")
        print("  • 'What's my current profile?'")
        print()
        console.info("Type 'quit' to exit, 'help' for more commands")
        console.divider()
        print()
        
        while True:
            try:
                user_input = input(console.user_prompt()).strip()
                
                if not user_input:
                    continue
                
                if user_input.lower() in ('quit', 'exit', 'q'):
                    console.info("Goodbye!")
                    break
                
                if user_input.lower() == 'help':
                    self._show_help()
                    continue
                
                if user_input.lower() == 'clear':
                    self.conversation_history.clear()
                    console.info("Conversation cleared")
                    continue
                
                if user_input.lower() == 'status':
                    self._show_status()
                    continue
                
                print()
                response = self.process_message(user_input)
                print()
                print(f"{console.ai_prompt()}{response}")
                print()
                
            except KeyboardInterrupt:
                print()
                console.info("Interrupted. Type 'quit' to exit.")
            except EOFError:
                break
            except Exception as e:
                console.error(f"Error: {e}")
    
    def _show_help(self):
        print()
        console.info("Available Commands:")
        print("  quit, exit, q  - Exit the assistant")
        print("  help           - Show this help")
        print("  clear          - Clear conversation history")
        print("  status         - Show connection status")
        print()
        console.info("Example Queries:")
        print("  • 'List all my mods'")
        print("  • 'Enable SKSE'")
        print("  • 'Disable texture mods'")
        print("  • 'Search for combat'")
        print()
    
    def _show_status(self):
        print()
        console.info("Status:")
        print(f"  MO2 Bridge: {'Connected' if self.client.connected else 'Disconnected'}")
        print(f"  AI Model: {self.config.gemini_model}")
        print(f"  Messages: {len(self.conversation_history)}")
        
        if self.client.connected:
            try:
                profile = self.client.call('organizer.profileName')
                mod_count = len(self.client.call('modList.allMods'))
                print(f"  Profile: {profile}")
                print(f"  Total Mods: {mod_count}")
            except:
                pass
        print()
    
    def shutdown(self):
        if self.client:
            self.client.disconnect()


# =============================================================================
# MAIN ENTRY POINT
# =============================================================================

def get_api_key() -> str:
    api_key = os.environ.get('GEMINI_API_KEY', '')
    
    if not api_key:
        console.warning("GEMINI_API_KEY environment variable not set")
        print()
        api_key = input("Enter your Gemini API key: ").strip()
        
        if not api_key:
            console.error("API key is required")
            sys.exit(1)
    
    return api_key


def print_banner():
    banner = """
╔══════════════════════════════════════════════════════════════╗
║                    MO2 AI Assistant                          ║
║              Powered by Google Gemini                        ║
╚══════════════════════════════════════════════════════════════╝
"""
    print(banner)


def main():
    print_banner()
    
    config = Config.from_env()
    config.gemini_api_key = get_api_key()
    
    assistant = MO2Assistant(config)
    
    try:
        if not assistant.initialize():
            console.error("Failed to initialize")
            sys.exit(1)
        
        assistant.run()
        
    except KeyboardInterrupt:
        print()
        console.info("Shutting down...")
    except Exception as e:
        console.error(f"Fatal error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
    finally:
        assistant.shutdown()


if __name__ == "__main__":
    main()