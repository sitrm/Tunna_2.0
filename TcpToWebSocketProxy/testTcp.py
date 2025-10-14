import socket
import time
import threading

def tcp_speed_test(host='127.0.0.1', port=5222, duration=10):
    test_data = b'X' * 2  # 1KB блок данных
    total_sent = 0
    start_time = time.time()
    
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(2)
        sock.connect((host, port))
        
        print(f"Testing speed to {host}:{port} for {duration} seconds...")
        
        while time.time() - start_time < duration:
            sent = sock.send(test_data)
            if sent == 0:
                break
            total_sent += sent
        
        elapsed = time.time() - start_time
        speed_mbps = (total_sent * 8) / elapsed / 1000000
        speed_mb_s = total_sent / elapsed / 1024 / 1024
        
        print(f"Time: {elapsed:.2f}s")
        print(f"Data sent: {total_sent / 1024 / 1024:.2f} MB")
        print(f"Speed: {speed_mbps:.2f} Mbps")
        print(f"Speed: {speed_mb_s:.2f} MB/s")
        
    except Exception as e:
        print(f"Error: {e}")
    finally:
        sock.close()

tcp_speed_test()