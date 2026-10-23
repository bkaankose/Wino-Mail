<?php
declare(strict_types=1);
require '/var/www/baikal/vendor/autoload.php';
use Symfony\Component\Yaml\Yaml;

$root = '/var/www/baikal';
$database = "$root/Specific/db/db.sqlite";
$password = '1';
if (!file_exists($database)) {
    if (!is_dir(dirname($database))) mkdir(dirname($database), 0775, true);
    $pdo = new PDO('sqlite:' . $database);
    $pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
    $pdo->exec(file_get_contents("$root/Core/Resources/Db/SQLite/db.sql"));
} else {
    $pdo = new PDO('sqlite:' . $database);
    $pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
}
if (!file_exists("$root/config/baikal.yaml")) {
    $config = [
        'system' => ['configured_version' => '0.10.1', 'timezone' => 'Europe/Warsaw',
            'card_enabled' => true, 'cal_enabled' => true, 'dav_auth_type' => 'Basic',
            'auth_realm' => 'Mail-Cal local lab', 'admin_passwordhash' => hash('sha256', "admin:Mail-Cal local lab:$password"),
            'base_uri' => '',
            'invite_from' => '', 'invite_from_name' => 'Mail-Cal lab'],
        'database' => ['backend' => 'sqlite', 'sqlite_file' => $database, 'encryption_key' => ''],
    ];
    file_put_contents("$root/config/baikal.yaml", Yaml::dump($config, 4));
}
$pdo->beginTransaction();
foreach (['alice' => 'alice@dovecot.test'] as $user => $email) {
    $stmt = $pdo->prepare('INSERT OR IGNORE INTO users (username,digesta1) VALUES (?,?)');
    $stmt->execute([$user, md5("$user:Mail-Cal local lab:$password")]);
    $stmt = $pdo->prepare('INSERT OR IGNORE INTO principals (uri,email,displayname) VALUES (?,?,?)');
    $stmt->execute(["principals/$user", $email, ucfirst($user) . ' Dovecot']);
}
$pdo->commit();
echo "Baikal configuration and users ready\n";
